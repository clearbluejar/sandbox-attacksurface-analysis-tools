﻿//  Copyright 2021 Google Inc. All Rights Reserved.
//
//  Licensed under the Apache License, Version 2.0 (the "License");
//  you may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
//
//  http://www.apache.org/licenses/LICENSE-2.0
//
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
//  limitations under the License.

using NtApiDotNet.Ndr.Marshal;
using NtApiDotNet.Win32.Rpc.Transport.PDU;
using NtApiDotNet.Win32.Security.Authentication;
using System;
using System.Collections.Generic;
using System.IO;

namespace NtApiDotNet.Win32.Rpc.Transport
{
    /// <summary>
    /// Base class for a DCE/RPC client transport. This implements the common functions
    /// of the DCE/RPC specs for network based RPC transports.
    /// </summary>
    public abstract class RpcDCEClientTransport : IRpcClientTransport
    {
        #region Protected Members

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="max_recv_fragment">The initial maximum receive fragment length.</param>
        /// <param name="max_send_fragment">The initial maximum send fragment length.</param>
        /// <param name="transport_security">The transport security for the connection.</param>
        /// <param name="data_rep">The data representation.</param>
        protected RpcDCEClientTransport(ushort max_recv_fragment, ushort max_send_fragment, 
            NdrDataRepresentation data_rep, RpcTransportSecurity transport_security)
        {
            _max_recv_fragment = max_recv_fragment;
            _max_send_fragment = max_send_fragment;
            _data_rep = data_rep;
            _transport_security = transport_security;
            _auth_context = transport_security.CreateClientContext();
        }

        /// <summary>
        /// Read the next fragment from the transport.
        /// </summary>
        /// <param name="max_recv_fragment">The maximum receive fragment length.</param>
        /// <returns>The read fragment.</returns>
        protected abstract byte[] ReadFragment(int max_recv_fragment);

        /// <summary>
        /// Write the fragment to the transport.
        /// </summary>
        /// <param name="fragment">The fragment to write.</param>
        /// <returns>True if successfully wrote the fragment.</returns>
        protected abstract bool WriteFragment(byte[] fragment);
        #endregion

        #region Private Members
        private readonly NdrDataRepresentation _data_rep;
        private readonly RpcTransportSecurity _transport_security;
        private ushort _max_recv_fragment;
        private ushort _max_send_fragment;
        private readonly ClientAuthenticationContext _auth_context;

        private PDUBase CheckFault(PDUBase pdu)
        {
            if (pdu is PDUShutdown)
            {
                Dispose();
                throw new RpcTransportException("Forced connection shutdown.");
            }

            if (pdu is PDUFault fault)
            {
                throw new RpcFaultException(fault.Status);
            }

            return pdu;
        }

        private PDUBase SendReceivePDU(int call_id, PDUBase send_pdu, AuthData auth_data, bool receive_pdu)
        {
            try
            {
                PDUHeader pdu_header = new PDUHeader()
                {
                    MajorVersion = PDUHeader.RPC_VERSION_MAJOR,
                    MinorVersion = PDUHeader.RPC_VERSION_MINOR,
                    DataRep = _data_rep,
                    CallId = CallId,
                    Type = send_pdu.PDUType,
                    Flags = PDUFlags.LastFrag | PDUFlags.FirstFrag
                };

                byte[] pdu_data = send_pdu.ToArray();
                pdu_header.FragmentLength = (ushort)(pdu_data.Length + PDUHeader.PDU_HEADER_SIZE);
                MemoryStream send_stm = new MemoryStream();
                BinaryWriter writer = new BinaryWriter(send_stm);
                pdu_header.Write(writer);
                writer.Write(pdu_data);
                byte[] fragment = send_stm.ToArray();
                RpcUtils.DumpBuffer(true, $"{GetType().Name} Send Buffer", fragment);
                if (!WriteFragment(fragment))
                    throw new RpcTransportException("Failed to write out PDU buffer.");

                if (!receive_pdu)
                    return null;

                var pdu = ReadPDU(0);
                var curr_header = pdu.Item1;
                if (!curr_header.Flags.HasFlagAllSet(PDUFlags.LastFrag | PDUFlags.FirstFrag))
                {
                    throw new RpcTransportException($"Invalid PDU flags {curr_header.Flags}.");
                }

                if (curr_header.CallId != call_id)
                {
                    throw new RpcTransportException($"Mismatching call ID - {curr_header.CallId} should be {call_id}.");
                }
                 
                return CheckFault(curr_header.ToPDU(pdu.Item2));
            }
            catch (EndOfStreamException)
            {
                throw new RpcTransportException("End of stream.");
            }
        }

        private Tuple<PDUHeader, byte[], AuthData> ReadPDU(int frag_count)
        {
            byte[] buffer = ReadFragment(_max_recv_fragment);
            RpcUtils.DumpBuffer(true, $"{GetType().Name} Receive Buffer - Fragment {frag_count}", buffer);
            MemoryStream stm = new MemoryStream(buffer);
            BinaryReader reader = new BinaryReader(stm);
            PDUHeader header = PDUHeader.Read(reader);
            NdrUnmarshalBuffer.CheckDataRepresentation(header.DataRep);
            AuthData auth_data = new AuthData();
            int auth_trailing_length = header.AuthLength > 0 ? header.AuthLength - AuthData.PDU_AUTH_DATA_HEADER_SIZE : 0;
            byte[] data = reader.ReadAllBytes(header.FragmentLength - PDUHeader.PDU_HEADER_SIZE - auth_trailing_length);
            if (auth_trailing_length > 0)
            {
                stm.Position = header.FragmentLength - auth_trailing_length;
                auth_data = AuthData.Read(reader, header.AuthLength);
            }
            return Tuple.Create(header, data, auth_data);
        }

        private PDUResponse SendReceiveRequestPDU(PDURequest send_pdu)
        {
            try
            {
                CallId++;
                PDUHeader pdu_header = new PDUHeader()
                {
                    MajorVersion = PDUHeader.RPC_VERSION_MAJOR,
                    MinorVersion = PDUHeader.RPC_VERSION_MINOR,
                    DataRep = _data_rep,
                    CallId = CallId,
                    Type = send_pdu.PDUType
                };

                List<byte[]> fragments = send_pdu.DoFragment(_max_send_fragment - PDUHeader.PDU_HEADER_SIZE);
                for (int i = 0; i < fragments.Count; ++i)
                {
                    pdu_header.Flags = send_pdu.HasObjectUUID ? PDUFlags.ObjectUuid : 0;
                    if (i == 0)
                    {
                        pdu_header.Flags |= PDUFlags.FirstFrag;
                    }
                    if (i == fragments.Count - 1)
                    {
                        pdu_header.Flags |= PDUFlags.LastFrag;
                    }

                    pdu_header.FragmentLength = (ushort)(fragments[i].Length + PDUHeader.PDU_HEADER_SIZE);
                    MemoryStream send_stm = new MemoryStream();
                    BinaryWriter writer = new BinaryWriter(send_stm);
                    pdu_header.Write(writer);

                    // TODO: Handle encryption/verification.
                    writer.Write(fragments[i]);
                    byte[] fragment = send_stm.ToArray();
                    string name = fragments.Count == 1 ? $"{GetType().Name} Send Buffer" : $"{GetType().Name} Send Buffer - Fragment {i}";
                    RpcUtils.DumpBuffer(true, name, fragment);
                    if (!WriteFragment(fragment))
                        throw new RpcTransportException("Failed to write out PDU buffer.");
                }

                MemoryStream recv_stm = new MemoryStream();
                PDUHeader curr_header = new PDUHeader();
                int frag_count = 0;
                while ((curr_header.Flags & PDUFlags.LastFrag) == 0)
                {
                    var pdu = ReadPDU(frag_count++);
                    curr_header = pdu.Item1;
                    if (curr_header.CallId != CallId)
                    {
                        throw new RpcTransportException("Mismatching call ID.");
                    }

                    // TODO: Handle encryption/verification.
                    recv_stm.Write(pdu.Item2, 0, pdu.Item2.Length);
                }

                var recv_pdu = CheckFault(curr_header.ToPDU(recv_stm.ToArray()));
                if (recv_pdu is PDUResponse pdu_respose)
                    return pdu_respose;

                throw new RpcTransportException($"Unexpected {recv_pdu.PDUType} PDU from server.");
            }
            catch (EndOfStreamException)
            {
                throw new RpcTransportException("End of stream.");
            }
        }
        #endregion

        #region IRpcClientTransport implementation.

        /// <summary>
        /// Get whether the client is connected or not.
        /// </summary>
        public abstract bool Connected { get; }

        /// <summary>
        /// Get the endpoint the client is connected to.
        /// </summary>
        public abstract string Endpoint { get; }

        /// <summary>
        /// Get the transport protocol sequence.
        /// </summary>
        public abstract string ProtocolSequence { get; }

        /// <summary>
        /// Bind the RPC transport to a specified interface.
        /// </summary>
        /// <param name="interface_id">The interface ID to bind to.</param>
        /// <param name="interface_version">The interface version to bind to.</param>
        /// <param name="transfer_syntax_id">The transfer syntax to use.</param>
        /// <param name="transfer_syntax_version">The transfer syntax version to use.</param>
        public void Bind(Guid interface_id, Version interface_version, Guid transfer_syntax_id, Version transfer_syntax_version)
        {
            if (transfer_syntax_id != Ndr.NdrNativeUtils.DCE_TransferSyntax || transfer_syntax_version != new Version(2, 0))
            {
                throw new ArgumentException("Only supports DCE transfer syntax");
            }

            PDUBind bind_pdu = new PDUBind(_max_send_fragment, _max_recv_fragment);
            bind_pdu.Elements.Add(new ContextElement(interface_id, interface_version, transfer_syntax_id, transfer_syntax_version));
            int call_id = ++CallId;
            var recv_pdu = SendReceivePDU(call_id, bind_pdu, new AuthData(), true);
            if (recv_pdu is PDUBindAck bind_ack)
            {
                if (bind_ack.ResultList.Count != 1 || bind_ack.ResultList[0].Result != PresentationResultType.Acceptance)
                {
                    throw new RpcTransportException($"Bind to {interface_id}:{interface_version} was rejected.");
                }

                _max_recv_fragment = bind_ack.MaxRecvFrag;
                _max_send_fragment = bind_ack.MaxXmitFrag;
            }
            else
            {
                throw new RpcTransportException("Unexpected PDU from server.");
            }
        }

        /// <summary>
        /// Send and receive an RPC message.
        /// </summary>
        /// <param name="proc_num">The procedure number.</param>
        /// <param name="objuuid">The object UUID for the call.</param>
        /// <param name="data_representation">NDR data representation.</param>
        /// <param name="ndr_buffer">Marshal NDR buffer for the call.</param>
        /// <param name="handles">List of handles marshaled into the buffer.</param>
        /// <returns>Client response from the send.</returns>
        public RpcClientResponse SendReceive(int proc_num, Guid objuuid, NdrDataRepresentation data_representation, byte[] ndr_buffer, IReadOnlyCollection<NtObject> handles)
        {
            NdrUnmarshalBuffer.CheckDataRepresentation(data_representation);

            PDURequest request = new PDURequest
            {
                OpNum = (short)proc_num,
                ObjectUUID = objuuid,
                StubData = ndr_buffer
            };

            return new RpcClientResponse(SendReceiveRequestPDU(request).StubData, new NtObject[0]);
        }

        /// <summary>
        /// Disconnect the transport.
        /// </summary>
        public abstract void Disconnect();
        #endregion

        #region IDisposable implementation.
        /// <summary>
        /// Dispose the transport.
        /// </summary>
        public virtual void Dispose()
        {
            _auth_context?.Dispose();
        }
        #endregion

        #region Public Properties
        /// <summary>
        /// Get the current Call ID.
        /// </summary>
        public int CallId { get; private set; }
        #endregion
    }
}