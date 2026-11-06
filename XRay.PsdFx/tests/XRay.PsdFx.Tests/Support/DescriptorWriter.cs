using System;
using System.Collections.Generic;

namespace XRay.PsdFx.Tests.Support
{
    /// <summary>
    /// Minimal Action Manager descriptor writer for synthetic lfx2/lrFX payloads:
    /// same surface the original suite uses inline (bool, doub, long, UntF, enum,
    /// Objc, VlLs), on top of <see cref="PsdBuilder.Writer"/>.
    /// </summary>
    internal sealed class DescriptorWriter
    {
        private readonly PsdBuilder.Writer _writer = new PsdBuilder.Writer();

        /// <summary>The eight-byte lfx2 header (version 0, descriptor version 16).</summary>
        public DescriptorWriter Header()
        {
            _writer.U32(0);
            _writer.U32(16);
            return this;
        }

        public PsdBuilder.Writer Writer
        {
            get { return _writer; }
        }

        public byte[] ToArray()
        {
            return _writer.ToArray();
        }

        public DescriptorWriter Descriptor(string classId, params KeyValuePair<string, object>[] items)
        {
            _writer.UnicodeString(string.Empty);
            _writer.DescriptorId(classId);
            _writer.U32((uint)items.Length);
            foreach (var item in items)
            {
                _writer.DescriptorId(item.Key);
                Value(item.Value);
            }

            return this;
        }

        public DescriptorWriter Value(object value)
        {
            if (value is bool)
            {
                _writer.Ascii("bool");
                _writer.U8((bool)value ? (byte)1 : (byte)0);
                return this;
            }

            if (value is double)
            {
                _writer.Ascii("doub");
                _writer.F64((double)value);
                return this;
            }

            if (value is int)
            {
                _writer.Ascii("long");
                _writer.I32((int)value);
                return this;
            }

            var unit = value as UnitValue;
            if (unit != null)
            {
                _writer.Ascii("UntF");
                _writer.Ascii(unit.Code);
                _writer.F64(unit.Value);
                return this;
            }

            var enumerated = value as EnumValue;
            if (enumerated != null)
            {
                _writer.Ascii("enum");
                _writer.DescriptorId(enumerated.Type);
                _writer.DescriptorId(enumerated.Value);
                return this;
            }

            var nested = value as NestedDescriptor;
            if (nested != null)
            {
                _writer.Ascii("Objc");
                return Descriptor(nested.ClassId, nested.Items);
            }

            var list = value as object[];
            if (list != null)
            {
                _writer.Ascii("VlLs");
                _writer.U32((uint)list.Length);
                foreach (var item in list)
                {
                    Value(item);
                }

                return this;
            }

            throw new ArgumentException("Unsupported descriptor value " + (value == null ? "null" : value.GetType().Name));
        }

        public static KeyValuePair<string, object> Item(string key, object value)
        {
            return new KeyValuePair<string, object>(key, value);
        }

        public static UnitValue Unit(string code, double value)
        {
            return new UnitValue(code, value);
        }

        public static EnumValue Enum(string type, string value)
        {
            return new EnumValue(type, value);
        }

        public static NestedDescriptor Obj(string classId, params KeyValuePair<string, object>[] items)
        {
            return new NestedDescriptor(classId, items);
        }

        internal sealed class UnitValue
        {
            public UnitValue(string code, double value)
            {
                Code = code;
                Value = value;
            }

            public string Code { get; private set; }

            public double Value { get; private set; }
        }

        internal sealed class EnumValue
        {
            public EnumValue(string type, string value)
            {
                Type = type;
                Value = value;
            }

            public string Type { get; private set; }

            public string Value { get; private set; }
        }

        internal sealed class NestedDescriptor
        {
            public NestedDescriptor(string classId, KeyValuePair<string, object>[] items)
            {
                ClassId = classId;
                Items = items;
            }

            public string ClassId { get; private set; }

            public KeyValuePair<string, object>[] Items { get; private set; }
        }
    }
}
