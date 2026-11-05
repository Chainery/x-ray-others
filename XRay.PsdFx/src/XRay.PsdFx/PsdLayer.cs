using System.Collections.Generic;
using System.Text;

namespace XRay.PsdFx
{
    /// <summary>A raw tagged block (8BIM signature, key, payload).</summary>
    public sealed class TaggedBlock
    {
        public TaggedBlock(string key, byte[] data)
        {
            Key = key;
            Data = data;
        }

        public string Key { get; private set; }

        public byte[] Data { get; private set; }
    }

    /// <summary>
    /// One layer record with its parsed layer style. Channel pixel data is not
    /// kept: this library reads structure and effects only.
    /// </summary>
    public sealed class PsdLayer
    {
        internal List<TaggedBlock> Blocks = new List<TaggedBlock>();

        public string Name { get; set; }

        public PsdRect Bounds { get; set; }

        public bool IsVisible { get; set; }

        public byte Opacity { get; set; }

        public PsdBlendMode BlendMode { get; set; }

        /// <summary>The parsed layer style, or null when the layer has no effects block.</summary>
        public PsdLayerStyle Style { get; set; }

        /// <summary>The keys of the layer's tagged blocks, in file order.</summary>
        public IReadOnlyList<string> TaggedBlockKeys
        {
            get
            {
                var keys = new List<string>(Blocks.Count);
                foreach (var block in Blocks)
                {
                    keys.Add(block.Key);
                }

                return keys;
            }
        }

        /// <summary>Returns the raw payload bytes of the first tagged block with this key, or null.</summary>
        public byte[] GetTaggedBlock(string key)
        {
            foreach (var block in Blocks)
            {
                if (block.Key == key)
                {
                    return block.Data;
                }
            }

            return null;
        }

        public override string ToString()
        {
            var builder = new StringBuilder(Name);
            if (Style != null && Style.Effects.Count > 0)
            {
                builder.Append(" (").Append(Style.Effects.Count).Append(" effects)");
            }

            return builder.ToString();
        }
    }
}
