using System;
using System.Collections.Generic;
using System.Text;

namespace UniMatrix.Services
{
    /// <summary>
    /// Minimal hand-rolled protobuf wire-format reader. Supports exactly what the LiveKit signalling
    /// messages need: varints, length-delimited fields (strings / bytes / embedded messages), 32/64-bit
    /// fixed fields, and skipping unknown fields. No generated code or reflection — we decode the few
    /// LiveKit messages we care about by field number directly. Protobuf wire types:
    /// 0=varint, 1=64-bit, 2=length-delimited, 5=32-bit.
    /// </summary>
    internal sealed class ProtoReader
    {
        private readonly byte[] _buf;
        private int _pos;
        private readonly int _end;

        public ProtoReader(byte[] buf) : this(buf, 0, buf?.Length ?? 0) { }

        public ProtoReader(byte[] buf, int offset, int length)
        {
            _buf = buf ?? new byte[0];
            _pos = offset;
            _end = offset + length;
        }

        /// <summary>True while there are more fields to read.</summary>
        public bool HasMore => _pos < _end;

        /// <summary>Reads the next field tag. Returns false at end of message. Outputs field number + wire type.</summary>
        public bool ReadTag(out int fieldNumber, out int wireType)
        {
            fieldNumber = 0;
            wireType = 0;
            if (_pos >= _end) return false;
            ulong tag = ReadVarint();
            fieldNumber = (int)(tag >> 3);
            wireType = (int)(tag & 0x7);
            return true;
        }

        public ulong ReadVarint()
        {
            ulong result = 0;
            int shift = 0;
            while (_pos < _end)
            {
                byte b = _buf[_pos++];
                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
                if (shift > 63) throw new FormatException("varint too long");
            }
            return result;
        }

        public long ReadInt64() => (long)ReadVarint();
        public int ReadInt32() => (int)ReadVarint();
        public bool ReadBool() => ReadVarint() != 0;
        public uint ReadUInt32() => (uint)ReadVarint();

        public byte[] ReadBytes()
        {
            int len = (int)ReadVarint();
            if (len < 0 || _pos + len > _end) throw new FormatException("length-delimited overruns buffer");
            var result = new byte[len];
            Array.Copy(_buf, _pos, result, 0, len);
            _pos += len;
            return result;
        }

        public string ReadString()
        {
            int len = (int)ReadVarint();
            if (len < 0 || _pos + len > _end) throw new FormatException("string overruns buffer");
            string s = Encoding.UTF8.GetString(_buf, _pos, len);
            _pos += len;
            return s;
        }

        /// <summary>Returns a reader scoped to the next length-delimited field (an embedded message).</summary>
        public ProtoReader ReadMessage()
        {
            int len = (int)ReadVarint();
            if (len < 0 || _pos + len > _end) throw new FormatException("embedded message overruns buffer");
            var sub = new ProtoReader(_buf, _pos, len);
            _pos += len;
            return sub;
        }

        /// <summary>Skips a field of the given wire type whose value we don't care about.</summary>
        public void SkipField(int wireType)
        {
            switch (wireType)
            {
                case 0: ReadVarint(); break;                 // varint
                case 1: _pos += 8; break;                    // 64-bit
                case 5: _pos += 4; break;                    // 32-bit
                case 2:                                       // length-delimited
                    int len = (int)ReadVarint();
                    _pos += len;
                    break;
                default:
                    throw new FormatException("unknown wire type " + wireType);
            }
            if (_pos > _end) _pos = _end;
        }
    }

    /// <summary>
    /// Minimal hand-rolled protobuf wire-format writer for the LiveKit signalling messages we send.
    /// Mirrors <see cref="ProtoReader"/>: varints, length-delimited fields, embedded messages.
    /// </summary>
    internal sealed class ProtoWriter
    {
        private readonly List<byte> _buf = new List<byte>(256);

        public byte[] ToArray() => _buf.ToArray();

        private void WriteRawByte(byte b) => _buf.Add(b);

        public void WriteVarint(ulong value)
        {
            while (value >= 0x80)
            {
                _buf.Add((byte)(value | 0x80));
                value >>= 7;
            }
            _buf.Add((byte)value);
        }

        private void WriteTag(int fieldNumber, int wireType)
        {
            WriteVarint((ulong)((fieldNumber << 3) | wireType));
        }

        public void WriteInt64(int fieldNumber, long value)
        {
            WriteTag(fieldNumber, 0);
            WriteVarint((ulong)value);
        }

        public void WriteInt32(int fieldNumber, int value)
        {
            WriteTag(fieldNumber, 0);
            WriteVarint((ulong)value);
        }

        public void WriteBool(int fieldNumber, bool value)
        {
            WriteTag(fieldNumber, 0);
            WriteVarint(value ? 1UL : 0UL);
        }

        /// <summary>Writes an enum value (varint), same wire encoding as int32.</summary>
        public void WriteEnum(int fieldNumber, int value) => WriteInt32(fieldNumber, value);

        public void WriteString(int fieldNumber, string value)
        {
            if (value == null) value = "";
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            WriteTag(fieldNumber, 2);
            WriteVarint((ulong)bytes.Length);
            _buf.AddRange(bytes);
        }

        public void WriteBytes(int fieldNumber, byte[] value)
        {
            if (value == null) value = new byte[0];
            WriteTag(fieldNumber, 2);
            WriteVarint((ulong)value.Length);
            _buf.AddRange(value);
        }

        /// <summary>Writes an embedded message (length-delimited) from its already-serialized bytes.</summary>
        public void WriteMessage(int fieldNumber, byte[] messageBytes)
        {
            if (messageBytes == null) messageBytes = new byte[0];
            WriteTag(fieldNumber, 2);
            WriteVarint((ulong)messageBytes.Length);
            _buf.AddRange(messageBytes);
        }
    }
}
