namespace UFS2Tool.Tests
{
    public class ValidationWriteStream : Stream
    {
        private readonly byte[] _expectedData;
        private long _position;

        public ValidationWriteStream(byte[] expectedData)
        {
            _expectedData = expectedData ?? throw new ArgumentNullException(nameof(expectedData));
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            for (int i = 0; i < count; i++)
            {
                long currentPos = _position + i;

                // Assert if trying to overflow
                Assert.True(currentPos < _expectedData.Length,
                    $"Function attempted to write past the expected length of {_expectedData.Length} bytes.");

                byte actualByte = buffer[offset + i];
                byte expectedByte = _expectedData[currentPos];

                // Assert if received byte is not expected value
                Assert.Equal(expectedByte, actualByte);
            }

            _position += count;
        }

        public void AssertComplete()
        {
            // Assert if we did not test entire array
            Assert.Equal(_expectedData.Length, _position);
        }

        // Required Stream boilerplate overrides
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _position;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Flush() { }
    }
}
