﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Text;
using Garnet.common;

namespace Garnet.server
{
    internal sealed unsafe partial class RespServerSession : ServerSessionBase
    {
        /// <summary>
        /// LPUSH key element[element...]
        /// RPUSH key element [element ...]
        /// </summary>
        /// <typeparam name="TGarnetApi"></typeparam>
        /// <param name="command"></param>
        /// <param name="storageApi"></param>
        /// <returns></returns>
        private unsafe bool ListPush<TGarnetApi>(RespCommand command, ref TGarnetApi storageApi)
                            where TGarnetApi : IGarnetApi
        {
            if (parseState.Count < 2)
            {
                return AbortWithWrongNumberOfArguments(command.ToString());
            }

            var sbKey = parseState.GetArgSliceByRef(0).SpanByte;
            var keyBytes = sbKey.ToByteArray();

            var lop =
                command switch
                {
                    RespCommand.LPUSH => ListOperation.LPUSH,
                    RespCommand.LPUSHX => ListOperation.LPUSHX,
                    RespCommand.RPUSH => ListOperation.RPUSH,
                    RespCommand.RPUSHX => ListOperation.RPUSHX,
                    _ => throw new Exception($"Unexpected {nameof(ListOperation)}: {command}")
                };

            // Prepare input
            var header = new RespInputHeader(GarnetObjectType.List) { ListOp = lop };
            var input = new ObjectInput(header, ref parseState, startIdx: 1);

            var status = command == RespCommand.LPUSH || command == RespCommand.LPUSHX
                ? storageApi.ListLeftPush(keyBytes, ref input, out var output)
                : storageApi.ListRightPush(keyBytes, ref input, out output);

            if (status == GarnetStatus.WRONGTYPE)
            {
                while (!RespWriteUtils.TryWriteError(CmdStrings.RESP_ERR_WRONG_TYPE, ref dcurr, dend))
                    SendAndReset();
            }
            else
            {
                // Write result to output
                while (!RespWriteUtils.TryWriteInt32(output.result1, ref dcurr, dend))
                    SendAndReset();
            }
            return true;
        }

        /// <summary>
        /// LPOP key [count]
        /// RPOP key [count]
        /// </summary>
        /// <param name="command"></param>
        /// <param name="storageApi"></param>
        /// <returns></returns>
        private unsafe bool ListPop<TGarnetApi>(RespCommand command, ref TGarnetApi storageApi)
                            where TGarnetApi : IGarnetApi
        {
            if (parseState.Count < 1)
            {
                return AbortWithWrongNumberOfArguments(command.ToString());
            }

            // Get the key for List
            var sbKey = parseState.GetArgSliceByRef(0).SpanByte;
            var keyBytes = sbKey.ToByteArray();

            var popCount = 1;

            if (parseState.Count == 2)
            {
                // Read count
                if (!parseState.TryGetInt(1, out popCount) || (popCount < 0))
                {
                    return AbortWithErrorMessage(CmdStrings.RESP_ERR_GENERIC_VALUE_IS_OUT_OF_RANGE);
                }
            }

            var lop =
                command switch
                {
                    RespCommand.LPOP => ListOperation.LPOP,
                    RespCommand.RPOP => ListOperation.RPOP,
                    _ => throw new Exception($"Unexpected {nameof(ListOperation)}: {command}")
                };

            // Prepare input
            var header = new RespInputHeader(GarnetObjectType.List) { ListOp = lop };
            var input = new ObjectInput(header, popCount);

            // Prepare GarnetObjectStore output
            var output = new GarnetObjectStoreOutput(new(dcurr, (int)(dend - dcurr)));

            var statusOp = command == RespCommand.LPOP
                ? storageApi.ListLeftPop(keyBytes, ref input, ref output)
                : storageApi.ListRightPop(keyBytes, ref input, ref output);

            switch (statusOp)
            {
                case GarnetStatus.OK:
                    //process output
                    ProcessOutput(output.SpanByteAndMemory);
                    break;
                case GarnetStatus.NOTFOUND:
                    WriteNull();
                    break;
                case GarnetStatus.WRONGTYPE:
                    while (!RespWriteUtils.TryWriteError(CmdStrings.RESP_ERR_WRONG_TYPE, ref dcurr, dend))
                        SendAndReset();
                    break;
            }

            return true;
        }

        /// <summary>
        /// The command returns the index of matching elements inside a Redis list.
        /// By default, when no options are given, it will scan the list from head to tail, looking for the first match of "element".
        /// </summary>
        /// <typeparam name="TGarnetApi"></typeparam>
        /// <param name="storageApi"></param>
        /// <returns></returns>
        private unsafe bool ListPosition<TGarnetApi>(ref TGarnetApi storageApi)
                            where TGarnetApi : IGarnetApi
        {
            if (parseState.Count < 2)
            {
                return AbortWithWrongNumberOfArguments(nameof(RespCommand.LPOS));
            }

            // Get the key for List
            var sbKey = parseState.GetArgSliceByRef(0).SpanByte;
            var keyBytes = sbKey.ToByteArray();

            // Prepare input
            var header = new RespInputHeader(GarnetObjectType.List) { ListOp = ListOperation.LPOS };
            var input = new ObjectInput(header, ref parseState, startIdx: 1);

            // Prepare GarnetObjectStore output
            var output = new GarnetObjectStoreOutput(new(dcurr, (int)(dend - dcurr)));

            var statusOp = storageApi.ListPosition(keyBytes, ref input, ref output);

            switch (statusOp)
            {
                case GarnetStatus.OK:
                    ProcessOutput(output.SpanByteAndMemory);
                    break;
                case GarnetStatus.NOTFOUND:
                    bool count = false;
                    for (var i = 2; i < parseState.Count; ++i)
                    {
                        if (parseState.GetArgSliceByRef(i).Span.EqualsUpperCaseSpanIgnoringCase(CmdStrings.COUNT))
                        {
                            count = true;
                            break;
                        }
                    }

                    if (count)
                    {
                        while (!RespWriteUtils.TryWriteEmptyArray(ref dcurr, dend))
                            SendAndReset();
                    }
                    else
                    {
                        WriteNull();
                    }
                    break;
                case GarnetStatus.WRONGTYPE:
                    while (!RespWriteUtils.TryWriteError(CmdStrings.RESP_ERR_WRONG_TYPE, ref dcurr, dend))
                        SendAndReset();
                    break;
            }

            return true;
        }

        /// <summary>
        /// LMPOP numkeys key [key ...] LEFT | RIGHT [COUNT count]
        /// </summary>
        /// <param name="storageApi"></param>
        /// <returns></returns>
        private unsafe bool ListPopMultiple<TGarnetApi>(ref TGarnetApi storageApi)
                            where TGarnetApi : IGarnetApi
        {
            if (parseState.Count < 3)
            {
                return AbortWithWrongNumberOfArguments("LMPOP");
            }

            var currTokenId = 0;

            // Read count of keys
            if (!parseState.TryGetInt(currTokenId++, out var numKeys))
            {
                var err = string.Format(CmdStrings.GenericErrShouldBeGreaterThanZero, "numkeys");
                return AbortWithErrorMessage(Encoding.ASCII.GetBytes(err));
            }

            if (parseState.Count != numKeys + 2 && parseState.Count != numKeys + 4)
            {
                return AbortWithErrorMessage(CmdStrings.RESP_ERR_GENERIC_SYNTAX_ERROR);
            }

            // Get the keys for Lists
            var keys = new ArgSlice[numKeys];

            for (var i = 0; i < keys.Length; i++)
            {
                keys[i] = parseState.GetArgSliceByRef(currTokenId++);
            }

            // Get the direction
            var dir = parseState.GetArgSliceByRef(currTokenId++);
            var popDirection = GetOperationDirection(dir);

            if (popDirection == OperationDirection.Unknown)
            {
                return AbortWithErrorMessage(CmdStrings.RESP_ERR_GENERIC_SYNTAX_ERROR);
            }

            var popCount = 1;

            // Get the COUNT keyword & parameter value, if specified
            if (parseState.Count == numKeys + 4)
            {
                var countKeyword = parseState.GetArgSliceByRef(currTokenId++);

                if (!countKeyword.ReadOnlySpan.EqualsUpperCaseSpanIgnoringCase(CmdStrings.COUNT))
                {
                    return AbortWithErrorMessage(CmdStrings.RESP_ERR_GENERIC_SYNTAX_ERROR);
                }

                // Read count
                if (!parseState.TryGetInt(currTokenId, out popCount))
                {
                    var err = string.Format(CmdStrings.GenericErrShouldBeGreaterThanZero, "count");
                    return AbortWithErrorMessage(Encoding.ASCII.GetBytes(err));
                }
            }

            var statusOp = popDirection == OperationDirection.Left
                ? storageApi.ListLeftPop(keys, popCount, out var key, out var elements)
                : storageApi.ListRightPop(keys, popCount, out key, out elements);

            switch (statusOp)
            {
                case GarnetStatus.OK:
                    while (!RespWriteUtils.TryWriteArrayLength(2, ref dcurr, dend))
                        SendAndReset();

                    while (!RespWriteUtils.TryWriteBulkString(key.Span, ref dcurr, dend))
                        SendAndReset();

                    while (!RespWriteUtils.TryWriteArrayLength(elements.Length, ref dcurr, dend))
                        SendAndReset();

                    foreach (var element in elements)
                    {
                        while (!RespWriteUtils.TryWriteBulkString(element.Span, ref dcurr, dend))
                            SendAndReset();
                    }

                    break;
                case GarnetStatus.NOTFOUND:
                    WriteNullArray();
                    break;
                case GarnetStatus.WRONGTYPE:
                    while (!RespWriteUtils.TryWriteError(CmdStrings.RESP_ERR_WRONG_TYPE, ref dcurr, dend))
                        SendAndReset();
                    break;
            }

            return true;
        }

        private bool ListBlockingPop(RespCommand command)
        {
            if (parseState.Count < 2)
            {
                return AbortWithWrongNumberOfArguments(command.ToString());
            }

            var keysBytes = new byte[parseState.Count - 1][];

            for (var i = 0; i < keysBytes.Length; i++)
            {
                keysBytes[i] = parseState.GetArgSliceByRef(i).SpanByte.ToByteArray();
            }

            if (!parseState.TryGetTimeout(parseState.Count - 1, out var timeout, out var error))
            {
                return AbortWithErrorMessage(error);
            }

            if (storeWrapper.objectStore == null)
                throw new GarnetException("Object store is disabled");

            var result = storeWrapper.itemBroker.GetCollectionItemAsync(command, keysBytes, this, timeout).Result;

            if (result.IsForceUnblocked)
            {
                while (!RespWriteUtils.TryWriteError(CmdStrings.RESP_UNBLOCKED_CLIENT_VIA_CLIENT_UNBLOCK, ref dcurr, dend))
                    SendAndReset();
                return true;
            }

            if (result.IsTypeMismatch)
            {
                while (!RespWriteUtils.TryWriteError(CmdStrings.RESP_ERR_WRONG_TYPE, ref dcurr, dend))
                    SendAndReset();
                return true;
            }

            if (!result.Found)
            {
                WriteNullArray();
            }
            else
            {
                while (!RespWriteUtils.TryWriteArrayLength(2, ref dcurr, dend))
                    SendAndReset();

                while (!RespWriteUtils.TryWriteBulkString(new Span<byte>(result.Key), ref dcurr, dend))
                    SendAndReset();

                while (!RespWriteUtils.TryWriteBulkString(new Span<byte>(result.Item), ref dcurr, dend))
                    SendAndReset();
            }

            return true;
        }

        private unsafe bool ListBlockingMove()
        {
            if (parseState.Count != 5)
            {
                return AbortWithWrongNumberOfArguments(nameof(RespCommand.BLMOVE));
            }

            var srcKey = parseState.GetArgSliceByRef(0);
            var dstKey = parseState.GetArgSliceByRef(1);
            var srcDir = parseState.GetArgSliceByRef(2);
            var dstDir = parseState.GetArgSliceByRef(3);

            if (!parseState.TryGetTimeout(4, out var timeout, out var error))
            {
                return AbortWithErrorMessage(error);
            }

            return ListBlockingMove(srcKey, dstKey, srcDir, dstDir, timeout);
        }

        /// <summary>
        /// BRPOPLPUSH
        /// </summary>
        /// <returns></returns>
        private bool ListBlockingPopPush()
        {
            if (parseState.Count != 3)
            {
                return AbortWithWrongNumberOfArguments(nameof(RespCommand.BRPOPLPUSH));
            }

            var srcKey = parseState.GetArgSliceByRef(0);
            var dstKey = parseState.GetArgSliceByRef(1);
            var rightOption = ArgSlice.FromPinnedSpan(CmdStrings.RIGHT);
            var leftOption = ArgSlice.FromPinnedSpan(CmdStrings.LEFT);

            if (!parseState.TryGetTimeout(2, out var timeout, out var error))
            {
                return AbortWithErrorMessage(error);
            }

            return ListBlockingMove(srcKey, dstKey, rightOption, leftOption, timeout);
        }

        private bool ListBlockingMove(ArgSlice srcKey, ArgSlice dstKey, ArgSlice srcDir, ArgSlice dstDir, double timeout)
        {
            var cmdArgs = new ArgSlice[] { default, default, default };

            // Read destination key
            cmdArgs[0] = dstKey;

            var sourceDirection = GetOperationDirection(srcDir);
            var destinationDirection = GetOperationDirection(dstDir);

            if (sourceDirection == OperationDirection.Unknown || destinationDirection == OperationDirection.Unknown)
            {
                return AbortWithErrorMessage(CmdStrings.RESP_ERR_GENERIC_SYNTAX_ERROR);
            }

            var pSrcDir = (byte*)&sourceDirection;
            var pDstDir = (byte*)&destinationDirection;
            cmdArgs[1] = new ArgSlice(pSrcDir, 1);
            cmdArgs[2] = new ArgSlice(pDstDir, 1);

            if (storeWrapper.objectStore == null)
                throw new GarnetException("Object store is disabled");

            var result = storeWrapper.itemBroker.MoveCollectionItemAsync(RespCommand.BLMOVE, srcKey.ToArray(), this, timeout,
                cmdArgs).Result;

            if (result.IsForceUnblocked)
            {
                while (!RespWriteUtils.TryWriteError(CmdStrings.RESP_UNBLOCKED_CLIENT_VIA_CLIENT_UNBLOCK, ref dcurr, dend))
                    SendAndReset();
                return true;
            }

            if (result.IsTypeMismatch)
            {
                while (!RespWriteUtils.TryWriteError(CmdStrings.RESP_ERR_WRONG_TYPE, ref dcurr, dend))
                    SendAndReset();
                return true;
            }

            if (!result.Found)
            {
                WriteNull();
            }
            else
            {
                while (!RespWriteUtils.TryWriteBulkString(new Span<byte>(result.Item), ref dcurr, dend))
                    SendAndReset();
            }

            return true;
        }

        /// <summary>
        /// LLEN key
        /// Gets the length of the list stored at key.
        /// </summary>
        /// <typeparam name="TGarnetApi"></typeparam>
        /// <param name="storageApi"></param>
        /// <returns></returns>
        private bool ListLength<TGarnetApi>(ref TGarnetApi storageApi)
                            where TGarnetApi : IGarnetApi
        {
            if (parseState.Count != 1)
            {
                return AbortWithWrongNumberOfArguments("LLEN");
            }

            var sbKey = parseState.GetArgSliceByRef(0).SpanByte;
            var keyBytes = sbKey.ToByteArray();

            // Prepare input
            var header = new RespInputHeader(GarnetObjectType.List) { ListOp = ListOperation.LLEN };
            var input = new ObjectInput(header);

            var status = storageApi.ListLength(keyBytes, ref input, out var output);

            switch (status)
            {
                case GarnetStatus.NOTFOUND:
                    while (!RespWriteUtils.TryWriteDirect(CmdStrings.RESP_RETURN_VAL_0, ref dcurr, dend))
                        SendAndReset();
                    break;
                case GarnetStatus.WRONGTYPE:
                    while (!RespWriteUtils.TryWriteError(CmdStrings.RESP_ERR_WRONG_TYPE, ref dcurr, dend))
                        SendAndReset();
                    break;
                default:
                    // Process output
                    while (!RespWriteUtils.TryWriteInt32(output.result1, ref dcurr, dend))
                        SendAndReset();
                    break;
            }

            return true;
        }

        /// <summary>
        /// LTRIM key start stop
        /// Trim an existing list so it only contains the specified range of elements.
        /// </summary>
        /// <typeparam name="TGarnetApi"></typeparam>
        /// <param name="storageApi"></param>
        /// <returns></returns>
        private bool ListTrim<TGarnetApi>(ref TGarnetApi storageApi)
                            where TGarnetApi : IGarnetApi
        {
            if (parseState.Count != 3)
            {
                return AbortWithWrongNumberOfArguments("LTRIM");
            }

            // Get the key for List
            var sbKey = parseState.GetArgSliceByRef(0).SpanByte;
            var keyBytes = sbKey.ToByteArray();

            // Read the parameters(start and stop) from LTRIM
            if (!parseState.TryGetInt(1, out var start) ||
                !parseState.TryGetInt(2, out var stop))
            {
                while (!RespWriteUtils.TryWriteError(CmdStrings.RESP_ERR_GENERIC_VALUE_IS_NOT_INTEGER, ref dcurr, dend))
                    SendAndReset();
                return true;
            }

            // Prepare input
            var header = new RespInputHeader(GarnetObjectType.List) { ListOp = ListOperation.LTRIM };
            var input = new ObjectInput(header, start, stop);

            var status = storageApi.ListTrim(keyBytes, ref input);

            switch (status)
            {
                case GarnetStatus.WRONGTYPE:
                    while (!RespWriteUtils.TryWriteError(CmdStrings.RESP_ERR_WRONG_TYPE, ref dcurr, dend))
                        SendAndReset();
                    break;
                default:
                    //GarnetStatus.OK or NOTFOUND have same result
                    // no need to process output, just send OK
                    while (!RespWriteUtils.TryWriteDirect(CmdStrings.RESP_OK, ref dcurr, dend))
                        SendAndReset();
                    break;
            }

            return true;
        }

        /// <summary>
        /// Gets the specified elements of the list stored at key.
        /// LRANGE key start stop
        /// </summary>
        /// <typeparam name="TGarnetApi"></typeparam>
        /// <param name="storageApi"></param>
        /// <returns></returns>
        private bool ListRange<TGarnetApi>(ref TGarnetApi storageApi)
             where TGarnetApi : IGarnetApi
        {
            if (parseState.Count != 3)
            {
                return AbortWithWrongNumberOfArguments("LRANGE");
            }

            // Get the key for List
            var sbKey = parseState.GetArgSliceByRef(0).SpanByte;
            var keyBytes = sbKey.ToByteArray();

            // Read count start and end params for LRANGE
            if (!parseState.TryGetInt(1, out var start) ||
                !parseState.TryGetInt(2, out var end))
            {
                while (!RespWriteUtils.TryWriteError(CmdStrings.RESP_ERR_GENERIC_VALUE_IS_NOT_INTEGER, ref dcurr, dend))
                    SendAndReset();
                return true;
            }

            // Prepare input
            var header = new RespInputHeader(GarnetObjectType.List) { ListOp = ListOperation.LRANGE };
            var input = new ObjectInput(header, start, end);

            // Prepare GarnetObjectStore output
            var output = new GarnetObjectStoreOutput(new(dcurr, (int)(dend - dcurr)));

            var statusOp = storageApi.ListRange(keyBytes, ref input, ref output);

            switch (statusOp)
            {
                case GarnetStatus.OK:
                    //process output
                    ProcessOutput(output.SpanByteAndMemory);
                    break;
                case GarnetStatus.NOTFOUND:
                    while (!RespWriteUtils.TryWriteDirect(CmdStrings.RESP_EMPTYLIST, ref dcurr, dend))
                        SendAndReset();
                    break;
                case GarnetStatus.WRONGTYPE:
                    while (!RespWriteUtils.TryWriteError(CmdStrings.RESP_ERR_WRONG_TYPE, ref dcurr, dend))
                        SendAndReset();
                    break;
            }
            return true;
        }

        /// <summary>
        /// Returns the element at index.
        /// LINDEX key index
        /// </summary>
        /// <typeparam name="TGarnetApi"></typeparam>
        /// <param name="storageApi"></param>
        /// <returns></returns>
        private bool ListIndex<TGarnetApi>(ref TGarnetApi storageApi)
             where TGarnetApi : IGarnetApi
        {
            if (parseState.Count != 2)
            {
                return AbortWithWrongNumberOfArguments("LINDEX");
            }

            // Get the key for List
            var sbKey = parseState.GetArgSliceByRef(0).SpanByte;
            var keyBytes = sbKey.ToByteArray();

            // Read index param
            if (!parseState.TryGetInt(1, out var index))
            {
                while (!RespWriteUtils.TryWriteError(CmdStrings.RESP_ERR_GENERIC_VALUE_IS_NOT_INTEGER, ref dcurr, dend))
                    SendAndReset();
                return true;
            }

            // Prepare input
            var header = new RespInputHeader(GarnetObjectType.List) { ListOp = ListOperation.LINDEX };
            var input = new ObjectInput(header, index);

            // Prepare GarnetObjectStore output
            var output = new GarnetObjectStoreOutput(new(dcurr, (int)(dend - dcurr)));

            var statusOp = storageApi.ListIndex(keyBytes, ref input, ref output);

            switch (statusOp)
            {
                case GarnetStatus.OK:
                    //process output
                    ProcessOutput(output.SpanByteAndMemory);
                    if (output.Header.result1 == -1)
                        WriteNull();
                    break;
                case GarnetStatus.NOTFOUND:
                    WriteNull();
                    break;
                case GarnetStatus.WRONGTYPE:
                    while (!RespWriteUtils.TryWriteError(CmdStrings.RESP_ERR_WRONG_TYPE, ref dcurr, dend))
                        SendAndReset();
                    break;
            }

            return true;
        }

        /// <summary>
        /// Inserts a new element in the list stored at key either before or after a value pivot
        /// LINSERT key BEFORE|AFTER pivot element
        /// </summary>
        /// <typeparam name="TGarnetApi"></typeparam>
        /// <param name="storageApi"></param>
        /// <returns></returns>
        private bool ListInsert<TGarnetApi>(ref TGarnetApi storageApi)
             where TGarnetApi : IGarnetApi
        {
            if (parseState.Count != 4)
            {
                return AbortWithWrongNumberOfArguments("LINSERT");
            }

            // Get the key for List
            var sbKey = parseState.GetArgSliceByRef(0).SpanByte;
            var keyBytes = sbKey.ToByteArray();

            // Prepare input
            var header = new RespInputHeader(GarnetObjectType.List) { ListOp = ListOperation.LINSERT };
            var input = new ObjectInput(header, ref parseState, startIdx: 1);

            var statusOp = storageApi.ListInsert(keyBytes, ref input, out var output);

            switch (statusOp)
            {
                case GarnetStatus.OK:
                    //check for partial execution
                    if (output.result1 == int.MinValue)
                        return false;
                    //process output
                    while (!RespWriteUtils.TryWriteInt32(output.result1, ref dcurr, dend))
                        SendAndReset();
                    break;
                case GarnetStatus.NOTFOUND:
                    while (!RespWriteUtils.TryWriteDirect(CmdStrings.RESP_RETURN_VAL_0, ref dcurr, dend))
                        SendAndReset();
                    break;
                case GarnetStatus.WRONGTYPE:
                    while (!RespWriteUtils.TryWriteError(CmdStrings.RESP_ERR_WRONG_TYPE, ref dcurr, dend))
                        SendAndReset();
                    break;
            }

            return true;
        }

        /// <summary>
        /// LREM key count element
        /// </summary>
        /// <typeparam name="TGarnetApi"></typeparam>
        /// <param name="storageApi"></param>
        /// <returns></returns>
        private bool ListRemove<TGarnetApi>(ref TGarnetApi storageApi)
              where TGarnetApi : IGarnetApi
        {
            // if params are missing return error
            if (parseState.Count != 3)
            {
                return AbortWithWrongNumberOfArguments("LREM");
            }

            // Get the key for List
            var sbKey = parseState.GetArgSliceByRef(0).SpanByte;
            var keyBytes = sbKey.ToByteArray();

            // Get count parameter
            if (!parseState.TryGetInt(1, out var nCount))
            {
                while (!RespWriteUtils.TryWriteError(CmdStrings.RESP_ERR_GENERIC_VALUE_IS_NOT_INTEGER, ref dcurr, dend))
                    SendAndReset();
                return true;
            }

            // Prepare input
            var header = new RespInputHeader(GarnetObjectType.List) { ListOp = ListOperation.LREM };
            var input = new ObjectInput(header, ref parseState, startIdx: 2, arg1: nCount);

            var statusOp = storageApi.ListRemove(keyBytes, ref input, out var output);

            switch (statusOp)
            {
                case GarnetStatus.OK:
                    //check for partial execution
                    if (output.result1 == int.MinValue)
                        return false;
                    //process output
                    while (!RespWriteUtils.TryWriteInt32(output.result1, ref dcurr, dend))
                        SendAndReset();
                    break;
                case GarnetStatus.NOTFOUND:
                    while (!RespWriteUtils.TryWriteDirect(CmdStrings.RESP_RETURN_VAL_0, ref dcurr, dend))
                        SendAndReset();
                    break;
                case GarnetStatus.WRONGTYPE:
                    while (!RespWriteUtils.TryWriteError(CmdStrings.RESP_ERR_WRONG_TYPE, ref dcurr, dend))
                        SendAndReset();
                    break;
            }

            return true;
        }

        /// <summary>
        /// LMOVE source destination [LEFT | RIGHT] [LEFT | RIGHT]
        /// </summary>
        /// <typeparam name="TGarnetApi"></typeparam>
        /// <param name="storageApi"></param>
        /// <returns></returns>
        private bool ListMove<TGarnetApi>(ref TGarnetApi storageApi)
             where TGarnetApi : IGarnetApi
        {
            if (parseState.Count != 4)
            {
                return AbortWithWrongNumberOfArguments("LMOVE");
            }

            var srcKey = parseState.GetArgSliceByRef(0);
            var dstKey = parseState.GetArgSliceByRef(1);

            var srcDirSlice = parseState.GetArgSliceByRef(2);
            var dstDirSlice = parseState.GetArgSliceByRef(3);

            var sourceDirection = GetOperationDirection(srcDirSlice);
            var destinationDirection = GetOperationDirection(dstDirSlice);

            if (sourceDirection == OperationDirection.Unknown || destinationDirection == OperationDirection.Unknown)
            {
                return AbortWithErrorMessage(CmdStrings.RESP_ERR_GENERIC_SYNTAX_ERROR);
            }

            if (!ListMove(srcKey, dstKey, sourceDirection, destinationDirection, out var node,
                    ref storageApi, out var garnetStatus))
                return false;

            switch (garnetStatus)
            {
                case GarnetStatus.OK:
                    if (node != null)
                    {
                        while (!RespWriteUtils.TryWriteBulkString(node, ref dcurr, dend))
                            SendAndReset();
                    }
                    else
                    {
                        WriteNull();
                    }

                    break;
                case GarnetStatus.WRONGTYPE:
                    while (!RespWriteUtils.TryWriteError(CmdStrings.RESP_ERR_WRONG_TYPE, ref dcurr, dend))
                        SendAndReset();
                    break;
            }

            return true;
        }

        /// <summary>
        /// RPOPLPUSH source destination
        /// </summary>
        /// <param name="storageApi"></param>
        /// <returns></returns>
        private bool ListRightPopLeftPush<TGarnetApi>(ref TGarnetApi storageApi)
            where TGarnetApi : IGarnetApi
        {
            if (parseState.Count != 2)
            {
                return AbortWithWrongNumberOfArguments("RPOPLPUSH");
            }

            var srcKey = parseState.GetArgSliceByRef(0);
            var dstKey = parseState.GetArgSliceByRef(1);

            if (!ListMove(srcKey, dstKey, OperationDirection.Right, OperationDirection.Left,
                    out var node, ref storageApi, out var garnetStatus))
                return false;

            switch (garnetStatus)
            {
                case GarnetStatus.OK:
                    if (node != null)
                    {
                        while (!RespWriteUtils.TryWriteBulkString(node, ref dcurr, dend))
                            SendAndReset();
                    }
                    else
                    {
                        WriteNull();
                    }

                    break;
                case GarnetStatus.WRONGTYPE:
                    while (!RespWriteUtils.TryWriteError(CmdStrings.RESP_ERR_WRONG_TYPE, ref dcurr, dend))
                        SendAndReset();
                    break;
            }

            return true;
        }

        /// <summary>
        /// LMOVE source destination LEFT|RIGHT LEFT|RIGHT
        /// RPOPLPUSH source destination
        /// </summary>
        /// <param name="sourceKey"></param>
        /// <param name="destinationKey"></param>
        /// <param name="sourceDirection"></param>
        /// <param name="destinationDirection"></param>
        /// <param name="node"></param>
        /// <param name="storageApi"></param>
        /// <param name="garnetStatus"></param>
        /// <returns></returns>
        private bool ListMove<TGarnetApi>(ArgSlice sourceKey, ArgSlice destinationKey,
            OperationDirection sourceDirection, OperationDirection destinationDirection, out byte[] node,
            ref TGarnetApi storageApi, out GarnetStatus garnetStatus)
            where TGarnetApi : IGarnetApi
        {
            garnetStatus = GarnetStatus.OK;
            node = null;

            garnetStatus =
                storageApi.ListMove(sourceKey, destinationKey, sourceDirection, destinationDirection, out node);
            return true;
        }

        /// <summary>
        /// Sets the list element at index to element
        /// LSET key index element
        /// </summary>
        /// <typeparam name="TGarnetApi"></typeparam>
        /// <param name="storageApi"></param>
        /// <returns></returns>
        public bool ListSet<TGarnetApi>(ref TGarnetApi storageApi)
            where TGarnetApi : IGarnetApi
        {
            if (parseState.Count != 3)
            {
                return AbortWithWrongNumberOfArguments("LSET");
            }

            // Get the key for List
            var sbKey = parseState.GetArgSliceByRef(0).SpanByte;
            var keyBytes = sbKey.ToByteArray();

            // Prepare input
            var header = new RespInputHeader(GarnetObjectType.List) { ListOp = ListOperation.LSET };
            var input = new ObjectInput(header, ref parseState, startIdx: 1);

            // Prepare GarnetObjectStore output
            var output = new GarnetObjectStoreOutput(new(dcurr, (int)(dend - dcurr)));

            var statusOp = storageApi.ListSet(keyBytes, ref input, ref output);

            switch (statusOp)
            {
                case GarnetStatus.OK:
                    //process output
                    ProcessOutput(output.SpanByteAndMemory);
                    break;
                case GarnetStatus.NOTFOUND:
                    while (!RespWriteUtils.TryWriteError(CmdStrings.RESP_ERR_GENERIC_NOSUCHKEY, ref dcurr, dend))
                        SendAndReset();
                    break;
                case GarnetStatus.WRONGTYPE:
                    while (!RespWriteUtils.TryWriteError(CmdStrings.RESP_ERR_WRONG_TYPE, ref dcurr, dend))
                        SendAndReset();
                    break;
            }

            return true;
        }

        /// <summary>
        /// BLMPOP timeout numkeys key [key ...] LEFT|RIGHT [COUNT count]
        /// </summary>
        /// <returns></returns>
        private unsafe bool ListBlockingPopMultiple()
        {
            if (parseState.Count < 4)
            {
                return AbortWithWrongNumberOfArguments(nameof(RespCommand.BLMPOP));
            }

            var currTokenId = 0;

            // Read timeout
            if (!parseState.TryGetTimeout(currTokenId++, out var timeout, out var error))
            {
                return AbortWithErrorMessage(error);
            }

            // Read count of keys
            if (!parseState.TryGetInt(currTokenId++, out var numKeys))
            {
                var err = string.Format(CmdStrings.GenericParamShouldBeGreaterThanZero, "numkeys");
                return AbortWithErrorMessage(Encoding.ASCII.GetBytes(err));
            }

            if (parseState.Count != numKeys + 3 && parseState.Count != numKeys + 5)
            {
                return AbortWithErrorMessage(CmdStrings.RESP_ERR_GENERIC_SYNTAX_ERROR);
            }

            // Get the keys for Lists
            var keysBytes = new byte[numKeys][];
            for (var i = 0; i < keysBytes.Length; i++)
            {
                keysBytes[i] = parseState.GetArgSliceByRef(currTokenId++).SpanByte.ToByteArray();
            }

            var cmdArgs = new ArgSlice[2];

            // Get the direction
            var dir = parseState.GetArgSliceByRef(currTokenId++);
            var popDirection = GetOperationDirection(dir);

            if (popDirection == OperationDirection.Unknown)
            {
                return AbortWithErrorMessage(CmdStrings.RESP_ERR_GENERIC_SYNTAX_ERROR);
            }
            cmdArgs[0] = new ArgSlice((byte*)&popDirection, 1);

            var popCount = 1;

            // Get the COUNT keyword & parameter value, if specified
            if (parseState.Count == numKeys + 5)
            {
                var countKeyword = parseState.GetArgSliceByRef(currTokenId++);

                if (!countKeyword.ReadOnlySpan.EqualsUpperCaseSpanIgnoringCase(CmdStrings.COUNT))
                {
                    return AbortWithErrorMessage(CmdStrings.RESP_ERR_GENERIC_SYNTAX_ERROR);
                }

                // Read count
                if (!parseState.TryGetInt(currTokenId, out popCount) || popCount < 1)
                {
                    var err = string.Format(CmdStrings.GenericParamShouldBeGreaterThanZero, "count");
                    return AbortWithErrorMessage(Encoding.ASCII.GetBytes(err));
                }
            }

            cmdArgs[1] = new ArgSlice((byte*)&popCount, sizeof(int));

            if (storeWrapper.objectStore == null)
                throw new GarnetException("Object store is disabled");

            var result = storeWrapper.itemBroker.GetCollectionItemAsync(RespCommand.BLMPOP, keysBytes, this, timeout, cmdArgs).Result;

            if (result.IsForceUnblocked)
            {
                while (!RespWriteUtils.TryWriteError(CmdStrings.RESP_UNBLOCKED_CLIENT_VIA_CLIENT_UNBLOCK, ref dcurr, dend))
                    SendAndReset();
            }

            if (result.IsTypeMismatch)
            {
                while (!RespWriteUtils.TryWriteError(CmdStrings.RESP_ERR_WRONG_TYPE, ref dcurr, dend))
                    SendAndReset();
                return true;
            }

            if (!result.Found)
            {
                WriteNull();
                return true;
            }

            while (!RespWriteUtils.TryWriteArrayLength(2, ref dcurr, dend))
                SendAndReset();

            while (!RespWriteUtils.TryWriteBulkString(result.Key, ref dcurr, dend))
                SendAndReset();

            var elements = result.Items;
            while (!RespWriteUtils.TryWriteArrayLength(elements.Length, ref dcurr, dend))
                SendAndReset();

            foreach (var element in elements)
            {
                while (!RespWriteUtils.TryWriteBulkString(element, ref dcurr, dend))
                    SendAndReset();
            }

            return true;
        }
    }
}