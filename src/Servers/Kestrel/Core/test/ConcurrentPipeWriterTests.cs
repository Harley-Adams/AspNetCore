// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Infrastructure.PipeWriterHelpers;
using Xunit;

namespace Microsoft.AspNetCore.Server.Kestrel.Core.Tests
{
    public class ConcurrentPipeWriterTests
    {
        [Fact]
        public async Task PassthroughIfAllFlushesAreAwaited()
        {
            using (var slabPool = new SlabMemoryPool())
            using (var diagnosticPool = new DiagnosticMemoryPool(slabPool))
            {
                var pipeWriterFlushTcsArray = new[] {
                    new TaskCompletionSource<FlushResult>(TaskCreationOptions.RunContinuationsAsynchronously),
                    new TaskCompletionSource<FlushResult>(TaskCreationOptions.RunContinuationsAsynchronously),
                };

                var mockPipeWriter = new MockPipeWriter(pipeWriterFlushTcsArray);
                var concurrentPipeWriter = new ConcurrentPipeWriter(mockPipeWriter, diagnosticPool, new object());

                var memory = concurrentPipeWriter.GetMemory();
                Assert.Equal(1, mockPipeWriter.GetMemoryCallCount);

                concurrentPipeWriter.Advance(memory.Length);
                Assert.Equal(1, mockPipeWriter.AdvanceCallCount);

                var flushTask0 = concurrentPipeWriter.FlushAsync();
                Assert.Equal(1, mockPipeWriter.FlushCallCount);

                pipeWriterFlushTcsArray[0].SetResult(default);

                await flushTask0.DefaultTimeout();

                memory = concurrentPipeWriter.GetMemory();
                Assert.Equal(2, mockPipeWriter.GetMemoryCallCount);

                concurrentPipeWriter.Advance(memory.Length);
                Assert.Equal(2, mockPipeWriter.AdvanceCallCount);

                var flushTask1 = concurrentPipeWriter.FlushAsync();
                Assert.Equal(2, mockPipeWriter.FlushCallCount);

                pipeWriterFlushTcsArray[1].SetResult(default);

                await flushTask1.DefaultTimeout();

                var completeEx = new Exception();
                await concurrentPipeWriter.CompleteAsync(completeEx).DefaultTimeout();
                Assert.Same(completeEx, mockPipeWriter.CompleteException);
            }
        }

        [Fact]
        public async Task QueuesIfFlushIsNotAwaited()
        {
            using (var slabPool = new SlabMemoryPool())
            using (var diagnosticPool = new DiagnosticMemoryPool(slabPool))
            {
                var pipeWriterFlushTcsArray = new[] {
                    new TaskCompletionSource<FlushResult>(TaskCreationOptions.RunContinuationsAsynchronously),
                    new TaskCompletionSource<FlushResult>(TaskCreationOptions.RunContinuationsAsynchronously),
                    new TaskCompletionSource<FlushResult>(TaskCreationOptions.RunContinuationsAsynchronously),
                };

                var mockPipeWriter = new MockPipeWriter(pipeWriterFlushTcsArray);
                var concurrentPipeWriter = new ConcurrentPipeWriter(mockPipeWriter, diagnosticPool, new object());

                var memory = concurrentPipeWriter.GetMemory();
                Assert.Equal(1, mockPipeWriter.GetMemoryCallCount);

                concurrentPipeWriter.Advance(memory.Length);
                Assert.Equal(1, mockPipeWriter.AdvanceCallCount);

                var flushTask0 = concurrentPipeWriter.FlushAsync();
                Assert.Equal(1, mockPipeWriter.FlushCallCount);

                Assert.False(flushTask0.IsCompleted);
                
                // Since the flush was not awaited, the following API calls are queued.
                memory = concurrentPipeWriter.GetMemory();
                concurrentPipeWriter.Advance(memory.Length);
                var flushTask1 = concurrentPipeWriter.FlushAsync();

                Assert.Equal(1, mockPipeWriter.GetMemoryCallCount);
                Assert.Equal(1, mockPipeWriter.AdvanceCallCount);
                Assert.Equal(1, mockPipeWriter.FlushCallCount);

                Assert.False(flushTask0.IsCompleted);
                Assert.False(flushTask1.IsCompleted);

                mockPipeWriter.FlushTcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
                pipeWriterFlushTcsArray[0].SetResult(default);
                await mockPipeWriter.FlushTcs.Task.DefaultTimeout();

                // Since the flush was not awaited, the following API calls are queued.
                memory = concurrentPipeWriter.GetMemory();
                concurrentPipeWriter.Advance(memory.Length);

                // We do not need to flush the final bytes, since the incomplete flush will pick it up.
                Assert.Equal(2, mockPipeWriter.GetMemoryCallCount);
                Assert.Equal(2, mockPipeWriter.AdvanceCallCount);
                Assert.Equal(2, mockPipeWriter.FlushCallCount);

                Assert.False(flushTask0.IsCompleted);
                Assert.False(flushTask1.IsCompleted);

                mockPipeWriter.FlushTcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
                pipeWriterFlushTcsArray[1].SetResult(default);
                await mockPipeWriter.FlushTcs.Task.DefaultTimeout();

                // Even though we only called flush on the ConcurrentPipeWriter twice, the inner PipeWriter was flushed three times.
                Assert.Equal(3, mockPipeWriter.GetMemoryCallCount);
                Assert.Equal(3, mockPipeWriter.AdvanceCallCount);
                Assert.Equal(3, mockPipeWriter.FlushCallCount);

                Assert.False(flushTask0.IsCompleted);
                Assert.False(flushTask1.IsCompleted);

                var completeEx = new Exception();
                await concurrentPipeWriter.CompleteAsync(completeEx);

                // Complete isn't called on the inner PipeWriter until the inner flushes have completed.
                Assert.Null(mockPipeWriter.CompleteException);

                pipeWriterFlushTcsArray[2].SetResult(default);

                await flushTask0.DefaultTimeout();
                await flushTask1.DefaultTimeout();

                Assert.Same(completeEx, mockPipeWriter.CompleteException);
            }
        }

        [Fact]
        public async Task KeepsQueueIfInnerFlushFinishesBetweenGetMemoryAndAdvance()
        {
            using (var slabPool = new SlabMemoryPool())
            using (var diagnosticPool = new DiagnosticMemoryPool(slabPool))
            {
                var pipeWriterFlushTcsArray = new[] {
                    new TaskCompletionSource<FlushResult>(TaskCreationOptions.RunContinuationsAsynchronously),
                    new TaskCompletionSource<FlushResult>(TaskCreationOptions.RunContinuationsAsynchronously),
                };

                var mockPipeWriter = new MockPipeWriter(pipeWriterFlushTcsArray);
                var concurrentPipeWriter = new ConcurrentPipeWriter(mockPipeWriter, diagnosticPool, new object());

                var memory = concurrentPipeWriter.GetMemory();
                Assert.Equal(1, mockPipeWriter.GetMemoryCallCount);

                concurrentPipeWriter.Advance(memory.Length);
                Assert.Equal(1, mockPipeWriter.AdvanceCallCount);

                var flushTask0 = concurrentPipeWriter.FlushAsync();
                Assert.Equal(1, mockPipeWriter.FlushCallCount);
                Assert.False(flushTask0.IsCompleted);

                // Only GetMemory() is called but not Advance() is not called yet when the first inner flush complets.
                memory = concurrentPipeWriter.GetMemory();

                // If the inner flush completes between a call to GetMemory() and Advance(), the outer
                // flush completes, and the next flush will pick up the buffered data.
                pipeWriterFlushTcsArray[0].SetResult(default);

                await flushTask0.DefaultTimeout();

                Assert.Equal(1, mockPipeWriter.GetMemoryCallCount);
                Assert.Equal(1, mockPipeWriter.AdvanceCallCount);
                Assert.Equal(1, mockPipeWriter.FlushCallCount);

                concurrentPipeWriter.Advance(memory.Length);
                memory = concurrentPipeWriter.GetMemory();
                concurrentPipeWriter.Advance(memory.Length);

                var flushTask1 = concurrentPipeWriter.FlushAsync();

                // Now that we flushed the ConcurrentPipeWriter again, the GetMemory() and Advance() calls are replayed.
                // Make sure that MockPipeWriter.SlabMemoryPoolBlockSize matches SlabMemoryPool._blockSize or else
                // it might take more or less calls to the inner PipeWriter's GetMemory method to copy all the data.
                Assert.Equal(3, mockPipeWriter.GetMemoryCallCount);
                Assert.Equal(3, mockPipeWriter.AdvanceCallCount);
                Assert.Equal(2, mockPipeWriter.FlushCallCount);
                Assert.False(flushTask1.IsCompleted);
 
                pipeWriterFlushTcsArray[1].SetResult(default);

                await flushTask1.DefaultTimeout();

                // Even though we only called flush on the ConcurrentPipeWriter twice, the inner PipeWriter was flushed three times.
                Assert.Equal(3, mockPipeWriter.GetMemoryCallCount);
                Assert.Equal(3, mockPipeWriter.AdvanceCallCount);
                Assert.Equal(2, mockPipeWriter.FlushCallCount);

                var completeEx = new Exception();
                await concurrentPipeWriter.CompleteAsync(completeEx);
                Assert.Same(completeEx, mockPipeWriter.CompleteException);
            }
        }

        [Fact]
        public async Task CompleteFlushesQueuedBytes()
        {
            using (var slabPool = new SlabMemoryPool())
            using (var diagnosticPool = new DiagnosticMemoryPool(slabPool))
            {
                var pipeWriterFlushTcsArray = new[] {
                    new TaskCompletionSource<FlushResult>(TaskCreationOptions.RunContinuationsAsynchronously),
                    new TaskCompletionSource<FlushResult>(TaskCreationOptions.RunContinuationsAsynchronously),
                };

                var mockPipeWriter = new MockPipeWriter(pipeWriterFlushTcsArray);
                var concurrentPipeWriter = new ConcurrentPipeWriter(mockPipeWriter, diagnosticPool, new object());

                var memory = concurrentPipeWriter.GetMemory();
                Assert.Equal(1, mockPipeWriter.GetMemoryCallCount);

                concurrentPipeWriter.Advance(memory.Length);
                Assert.Equal(1, mockPipeWriter.AdvanceCallCount);

                var flushTask0 = concurrentPipeWriter.FlushAsync();
                Assert.Equal(1, mockPipeWriter.FlushCallCount);
                Assert.False(flushTask0.IsCompleted);

                // Only GetMemory() is called but not Advance() is not called yet when the first inner flush completes.
                memory = concurrentPipeWriter.GetMemory();

                // If the inner flush completes between a call to GetMemory() and Advance(), the outer
                // flush completes, and the next flush will pick up the buffered data.
                pipeWriterFlushTcsArray[0].SetResult(default);

                await flushTask0.DefaultTimeout();

                Assert.Equal(1, mockPipeWriter.GetMemoryCallCount);
                Assert.Equal(1, mockPipeWriter.AdvanceCallCount);
                Assert.Equal(1, mockPipeWriter.FlushCallCount);

                concurrentPipeWriter.Advance(memory.Length);
                memory = concurrentPipeWriter.GetMemory();
                concurrentPipeWriter.Advance(memory.Length);

                // Complete the ConcurrentPipeWriter without flushing any of the queued data.
                var completeEx = new Exception();
                await concurrentPipeWriter.CompleteAsync(completeEx);

                // Now that we completed the ConcurrentPipeWriter, the GetMemory() and Advance() calls are replayed.
                // Make sure that MockPipeWriter.SlabMemoryPoolBlockSize matches SlabMemoryPool._blockSize or else
                // it might take more or less calls to the inner PipeWriter's GetMemory method to copy all the data.
                Assert.Equal(3, mockPipeWriter.GetMemoryCallCount);
                Assert.Equal(3, mockPipeWriter.AdvanceCallCount);
                Assert.Equal(1, mockPipeWriter.FlushCallCount);
                Assert.Same(completeEx, mockPipeWriter.CompleteException);
            }
        }

        [Fact]
        public async Task CancelPendingFlushInterruptsFlushLoop()
        {
            using (var slabPool = new SlabMemoryPool())
            using (var diagnosticPool = new DiagnosticMemoryPool(slabPool))
            {
                var pipeWriterFlushTcsArray = new[] {
                    new TaskCompletionSource<FlushResult>(TaskCreationOptions.RunContinuationsAsynchronously),
                    new TaskCompletionSource<FlushResult>(TaskCreationOptions.RunContinuationsAsynchronously),
                };

                var mockPipeWriter = new MockPipeWriter(pipeWriterFlushTcsArray);
                var concurrentPipeWriter = new ConcurrentPipeWriter(mockPipeWriter, diagnosticPool, new object());

                var memory = concurrentPipeWriter.GetMemory();
                Assert.Equal(1, mockPipeWriter.GetMemoryCallCount);

                concurrentPipeWriter.Advance(memory.Length);
                Assert.Equal(1, mockPipeWriter.AdvanceCallCount);

                var flushTask0 = concurrentPipeWriter.FlushAsync();
                Assert.Equal(1, mockPipeWriter.FlushCallCount);

                Assert.False(flushTask0.IsCompleted);
                
                // Since the flush was not awaited, the following API calls are queued.
                memory = concurrentPipeWriter.GetMemory();
                concurrentPipeWriter.Advance(memory.Length);
                var flushTask1 = concurrentPipeWriter.FlushAsync();

                Assert.Equal(1, mockPipeWriter.GetMemoryCallCount);
                Assert.Equal(1, mockPipeWriter.AdvanceCallCount);
                Assert.Equal(1, mockPipeWriter.FlushCallCount);

                Assert.False(flushTask0.IsCompleted);
                Assert.False(flushTask1.IsCompleted);

                // CancelPendingFlush() does not get queued.
                concurrentPipeWriter.CancelPendingFlush();
                Assert.Equal(1, mockPipeWriter.CancelPendingFlushCallCount);

                pipeWriterFlushTcsArray[0].SetResult(new FlushResult(isCanceled: true, isCompleted: false));

                Assert.True((await flushTask0.DefaultTimeout()).IsCanceled);
                Assert.True((await flushTask1.DefaultTimeout()).IsCanceled);

                var flushTask2 = concurrentPipeWriter.FlushAsync();
                Assert.False(flushTask2.IsCompleted);

                pipeWriterFlushTcsArray[1].SetResult(default);
                await flushTask2.DefaultTimeout();

                // We do not need to flush the final bytes, since the incomplete flush will pick it up.
                Assert.Equal(2, mockPipeWriter.GetMemoryCallCount);
                Assert.Equal(2, mockPipeWriter.AdvanceCallCount);
                Assert.Equal(2, mockPipeWriter.FlushCallCount);

                var completeEx = new Exception();
                await concurrentPipeWriter.CompleteAsync(completeEx);
                Assert.Same(completeEx, mockPipeWriter.CompleteException);
            }
        }

        private class MockPipeWriter : PipeWriter
        {
            // It's important that this matches SlabMemoryPool._blockSize for all the tests to pass.
            private const int SlabMemoryPoolBlockSize = 4096;

            private readonly TaskCompletionSource<FlushResult>[] _flushResults;

            public MockPipeWriter(TaskCompletionSource<FlushResult>[] flushResults)
            {
                _flushResults = flushResults;
            }

            public int GetMemoryCallCount { get; set; }
            public int AdvanceCallCount { get; set; }
            public int FlushCallCount { get; set; }
            public int CancelPendingFlushCallCount { get; set; }

            public TaskCompletionSource<object> FlushTcs { get; set; }

            public Exception CompleteException { get; set; }

            public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
            {
                FlushCallCount++;
                FlushTcs?.TrySetResult(null);
                return new ValueTask<FlushResult>(_flushResults[FlushCallCount - 1].Task);
            }

            public override Memory<byte> GetMemory(int sizeHint = 0)
            {
                GetMemoryCallCount++;
                return new Memory<byte>(new byte[sizeHint == 0 ? SlabMemoryPoolBlockSize : sizeHint]);
            }

            public override Span<byte> GetSpan(int sizeHint = 0)
            {
                return GetMemory(sizeHint).Span;
            }

            public override void Advance(int bytes)
            {
                AdvanceCallCount++;
            }

            public override void Complete(Exception exception = null)
            {
                CompleteException = exception;
            }

            public override void CancelPendingFlush()
            {
                CancelPendingFlushCallCount++;
            }
        }
    }
}
