﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System.Text;
using BenchmarkDotNet.Attributes;
using Garnet.cluster;
using Garnet.common;

namespace BDN.benchmark.Cluster
{
    /// <summary>
    /// Cluster migrate benchmark
    /// </summary>
    [MemoryDiagnoser]
    public unsafe class ClusterMigrate
    {
        /// <summary>
        /// Cluster parameters
        /// </summary>
        [ParamsSource(nameof(ClusterParamsProvider))]
        public ClusterParams Params { get; set; }

        /// <summary>
        /// Cluster parameters provider
        /// </summary>
        public IEnumerable<ClusterParams> ClusterParamsProvider()
        {
            yield return new(false);
        }

        ClusterContext cc;

        [GlobalSetup]
        public void GlobalSetup()
        {
            cc = new ClusterContext();
            cc.SetupSingleInstance();
            cc.AddSlotRange([(0, 16383)]);
            cc.CreateGetSet();
            cc.CreateMGetMSet();

            cc.Consume(cc.singleGetSet[1].ptr, cc.singleGetSet[1].buffer.Length);
            cc.Consume(cc.singleMGetMSet[1].ptr, cc.singleMGetMSet[1].buffer.Length);

            // Initiate fake gossip to add a fake node
            var config = new ClusterConfig().InitializeLocalWorker(
                Generator.CreateHexId(),
                "127.0.0.1",
                7001,
                configEpoch: 0,
                NodeRole.PRIMARY,
                null,
                "");
            var configBytes = config.ToByteArray();
            var gossipHeader = Encoding.ASCII.GetBytes($"*4\r\n$7\r\nCLUSTER\r\n$6\r\nGOSSIP\r\n$8\r\nWITHMEET\r\n${configBytes.Length}\r\n");
            var gossipReq = new byte[gossipHeader.Length + configBytes.Length + 2];
            Buffer.BlockCopy(gossipHeader, 0, gossipReq, 0, gossipHeader.Length);
            Buffer.BlockCopy(configBytes, 0, gossipReq, gossipHeader.Length, configBytes.Length);
            Buffer.BlockCopy(Encoding.ASCII.GetBytes("\r\n"), 0, gossipReq, gossipHeader.Length + configBytes.Length, 2);

            fixed (byte* req = gossipReq)
                cc.Consume(req, gossipReq.Length);

            // Set slot to migrating state
            var slot = HashSlotUtils.HashSlot(ClusterContext.keyTag.ToArray());
            SetSlot(slot, "MIGRATING", config.LocalNodeId);
        }

        private void SetSlot(int slot, string state, string nodeId)
        {
            var reqBytes = "*5\r\n"u8.Length + 4 + "CLUSTER\r\n"u8.Length + 4 + "SETSLOT\r\n"u8.Length +
                1 + NumUtils.CountDigits(slot.ToString().Length) + 2 + slot.ToString().Length + 2 +
                1 + NumUtils.CountDigits(state.Length) + 2 + state.Length + 2 +
                1 + NumUtils.CountDigits(nodeId.Length) + 2 + nodeId.Length + 2;

            var setSlotReq = new Request(reqBytes);
            var curr = setSlotReq.ptr;
            var end = curr + setSlotReq.buffer.Length;
            _ = RespWriteUtils.TryWriteArrayLength(5, ref curr, end);
            _ = RespWriteUtils.TryWriteBulkString("CLUSTER"u8, ref curr, end);
            _ = RespWriteUtils.TryWriteBulkString("SETSLOT"u8, ref curr, end);
            _ = RespWriteUtils.TryWriteInt32AsBulkString(slot, ref curr, end);
            _ = RespWriteUtils.TryWriteBulkString(Encoding.ASCII.GetBytes(state), ref curr, end);
            _ = RespWriteUtils.TryWriteBulkString(Encoding.ASCII.GetBytes(nodeId), ref curr, end);
            cc.Consume(setSlotReq.ptr, setSlotReq.buffer.Length);
        }

        [GlobalCleanup]
        public void GlobalCleanup()
        {
            cc.Dispose();
        }

        [Benchmark]
        public void Get()
        {
            cc.Consume(cc.singleGetSet[0].ptr, cc.singleGetSet[0].buffer.Length);
        }

        [Benchmark]
        public void Set()
        {
            cc.Consume(cc.singleGetSet[1].ptr, cc.singleGetSet[1].buffer.Length);
        }

        [Benchmark]
        public void MGet()
        {
            cc.Consume(cc.singleMGetMSet[0].ptr, cc.singleMGetMSet[0].buffer.Length);
        }

        [Benchmark]
        public void MSet()
        {
            cc.Consume(cc.singleMGetMSet[1].ptr, cc.singleMGetMSet[1].buffer.Length);
        }
    }
}