﻿using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Channels.Tests
{
    public class UnownedBufferChannelFacts
    {
        [Fact]
        public async Task CanConsumeData()
        {
            var stream = new CallbackStream(async (s, token) =>
            {
                var sw = new StreamWriter(s);
                await sw.WriteAsync("Hello");
                await sw.FlushAsync();
                await sw.WriteAsync("World");
                await sw.FlushAsync();
            });

            var channel = stream.AsReadableChannel();

            int calls = 0;

            while (true)
            {
                var result = await channel.ReadAsync();
                var buffer = result.Buffer;
                calls++;
                if (buffer.IsEmpty && result.IsCompleted)
                {
                    // Done
                    break;
                }

                var segment = buffer.ToArray();

                var data = Encoding.UTF8.GetString(segment);
                if (calls == 1)
                {
                    Assert.Equal("Hello", data);
                }
                else
                {
                    Assert.Equal("World", data);
                }

                channel.Advance(buffer.End);
            }
        }

        [Fact]
        public async Task CanCancelConsumingData()
        {
            var cts = new CancellationTokenSource();
            var stream = new CallbackStream(async (s, token) =>
            {
                var hello = Encoding.UTF8.GetBytes("Hello");
                var world = Encoding.UTF8.GetBytes("World");
                await s.WriteAsync(hello, 0, hello.Length, token);
                cts.Cancel();
                await s.WriteAsync(world, 0, world.Length, token);
            });

            var channel = stream.AsReadableChannel(cts.Token);

            int calls = 0;

            while (true)
            {
                ChannelReadResult result;
                ReadableBuffer buffer;
                try
                {
                    result = await channel.ReadAsync();
                    buffer = result.Buffer;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                finally
                {
                    calls++;
                }
                if (buffer.IsEmpty && result.IsCompleted)
                {
                    // Done
                    break;
                }

                var segment = buffer.ToArray();

                var data = Encoding.UTF8.GetString(segment);
                Assert.Equal("Hello", data);

                channel.Advance(buffer.End);
            }

            Assert.Equal(2, calls);
        }

        [Fact]
        public async Task CanConsumeLessDataThanProduced()
        {
            var stream = new CallbackStream(async (s, token) =>
            {
                var sw = new StreamWriter(s);
                await sw.WriteAsync("Hello ");
                await sw.FlushAsync();
                await sw.WriteAsync("World");
                await sw.FlushAsync();
            });

            var channel = stream.AsReadableChannel();

            int index = 0;
            var message = "Hello World";

            while (true)
            {
                var result = await channel.ReadAsync();
                var buffer = result.Buffer;

                if (buffer.IsEmpty && result.IsCompleted)
                {
                    // Done
                    break;
                }

                var ch = (char)buffer.First.Span[0];
                Assert.Equal(message[index++], ch);
                channel.Advance(buffer.Start.Seek(1));
            }

            Assert.Equal(message.Length, index);
        }

        [Fact]
        public async Task AccessingUnownedMemoryThrowsIfUsedAfterAdvance()
        {
            var stream = new CallbackStream(async (s, token) =>
            {
                var sw = new StreamWriter(s);
                await sw.WriteAsync("Hello ");
                await sw.FlushAsync();
            });

            var channel = stream.AsReadableChannel();

            var data = Memory<byte>.Empty;

            while (true)
            {
                var result = await channel.ReadAsync();
                var buffer = result.Buffer;

                if (buffer.IsEmpty && result.IsCompleted)
                {
                    // Done
                    break;
                }
                data = buffer.First;
                channel.Advance(buffer.End);
            }

            Assert.Throws<ObjectDisposedException>(() => data.Span);
        }

        [Fact]
        public async Task PreservingUnownedBufferCopies()
        {
            var stream = new CallbackStream(async (s, token) =>
            {
                var sw = new StreamWriter(s);
                await sw.WriteAsync("Hello ");
                await sw.FlushAsync();
            });

            var channel = stream.AsReadableChannel();

            var preserved = default(PreservedBuffer);

            while (true)
            {
                var result = await channel.ReadAsync();
                var buffer = result.Buffer;

                if (buffer.IsEmpty && result.IsCompleted)
                {
                    // Done
                    break;
                }

                preserved = buffer.Preserve();

                // Make sure we can acccess the span
                var span = buffer.First.Span;

                channel.Advance(buffer.End);
            }

            using (preserved)
            {
                Assert.Equal("Hello ", Encoding.UTF8.GetString(preserved.Buffer.ToArray()));
            }

            Assert.Throws<ObjectDisposedException>(() => preserved.Buffer.First.Span);
        }

        [Fact]
        public async Task CanConsumeLessDataThanProducedAndPreservingOwnedBuffers()
        {
            var stream = new CallbackStream(async (s, token) =>
            {
                var sw = new StreamWriter(s);
                await sw.WriteAsync("Hello ");
                await sw.FlushAsync();
                await sw.WriteAsync("World");
                await sw.FlushAsync();
            });

            var channel = stream.AsReadableChannel();

            int index = 0;
            var message = "Hello World";

            while (true)
            {
                var result = await channel.ReadAsync();
                var buffer = result.Buffer;

                if (buffer.IsEmpty && result.IsCompleted)
                {
                    // Done
                    break;
                }

                using (buffer.Preserve())
                {
                    var ch = (char)buffer.First.Span[0];
                    Assert.Equal(message[index++], ch);
                    channel.Advance(buffer.Start.Seek(1), buffer.End);
                }
            }

            Assert.Equal(message.Length, index);
        }

        [Fact]
        public async Task CanConsumeLessDataThanProducedAndPreservingUnOwnedBuffers()
        {
            var stream = new CallbackStream(async (s, token) =>
            {
                var sw = new StreamWriter(s);
                await sw.WriteAsync("Hello ");
                await sw.FlushAsync();
                await sw.WriteAsync("World");
                await sw.FlushAsync();
            });

            var channel = stream.AsReadableChannel();

            int index = 0;
            var message = "Hello World";

            while (true)
            {
                var result = await channel.ReadAsync();
                var buffer = result.Buffer;

                if (buffer.IsEmpty && result.IsCompleted)
                {
                    // Done
                    break;
                }

                using (buffer.Preserve())
                {
                    var ch = (char)buffer.First.Span[0];
                    Assert.Equal(message[index++], ch);
                    channel.Advance(buffer.Start.Seek(1));
                }
            }

            Assert.Equal(message.Length, index);
        }

        [Fact]
        public async Task CanConsumeLessDataThanProducedWithBufferReuse()
        {
            var stream = new CallbackStream(async (s, token) =>
            {
                var data = new byte[4096];
                Encoding.UTF8.GetBytes("Hello ", 0, 6, data, 0);
                await s.WriteAsync(data, 0, 6);
                Encoding.UTF8.GetBytes("World", 0, 5, data, 0);
                await s.WriteAsync(data, 0, 5);
            });

            var channel = stream.AsReadableChannel();

            int index = 0;
            var message = "Hello World";

            while (index <= message.Length)
            {
                var result = await channel.ReadAsync();
                var buffer = result.Buffer;

                var ch = Encoding.UTF8.GetString(buffer.Slice(0, index).ToArray());
                Assert.Equal(message.Substring(0, index), ch);

                // Never consume, to force buffers to be copied
                channel.Advance(buffer.Start, buffer.Start.Seek(index));

                // Yield the task. This will ensure that we don't have any Tasks idling
                // around in UnownedBufferChannel.OnCompleted
                await Task.Yield();

                index++;
            }

            Assert.Equal(message.Length + 1, index);
        }

        [Fact]
        public async Task NotCallingAdvanceWillCauseReadToThrow()
        {
            var stream = new CallbackStream(async (s, token) =>
            {
                var sw = new StreamWriter(s);
                await sw.WriteAsync("Hello");
                await sw.FlushAsync();
                await sw.WriteAsync("World");
                await sw.FlushAsync();
            });

            var channel = stream.AsReadableChannel();

            int calls = 0;

            InvalidOperationException thrown = null;
            while (true)
            {
                ChannelReadResult result;
                ReadableBuffer buffer;
                try
                {
                    result = await channel.ReadAsync();
                    buffer = result.Buffer;
                }
                catch (InvalidOperationException ex)
                {
                    thrown = ex;
                    break;
                }

                calls++;
                if (buffer.IsEmpty && result.IsCompleted)
                {
                    // Done
                    break;
                }

                var segment = buffer.ToArray();

                var data = Encoding.UTF8.GetString(segment);
                if (calls == 1)
                {
                    Assert.Equal("Hello", data);
                }
                else
                {
                    Assert.Equal("World", data);
                }
            }
            Assert.Equal(1, calls);
            Assert.NotNull(thrown);
            Assert.Equal("Cannot Read until the previous read has been acknowledged by calling Advance", thrown.Message);
        }

        private class CallbackStream : Stream
        {
            private readonly Func<Stream, CancellationToken, Task> _callback;
            public CallbackStream(Func<Stream, CancellationToken, Task> callback)
            {
                _callback = callback;
            }

            public override bool CanRead
            {
                get
                {
                    throw new NotImplementedException();
                }
            }

            public override bool CanSeek
            {
                get
                {
                    throw new NotImplementedException();
                }
            }

            public override bool CanWrite
            {
                get
                {
                    throw new NotImplementedException();
                }
            }

            public override long Length
            {
                get
                {
                    throw new NotImplementedException();
                }
            }

            public override long Position
            {
                get
                {
                    throw new NotImplementedException();
                }

                set
                {
                    throw new NotImplementedException();
                }
            }

            public override void Flush()
            {
                throw new NotImplementedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                throw new NotImplementedException();
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotImplementedException();
            }

            public override void SetLength(long value)
            {
                throw new NotImplementedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotImplementedException();
            }

            public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
            {
                return _callback(destination, cancellationToken);
            }
        }
    }
}
