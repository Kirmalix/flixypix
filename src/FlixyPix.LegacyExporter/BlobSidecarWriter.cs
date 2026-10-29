using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace FlixyPix.LegacyExporter
{
    // This file is also compiled by provider-free host tests. It never references SQL CE.
    internal sealed class BlobSidecarWriter
    {
        internal const int BufferBytes = 1024 * 1024;
        private readonly string directory;
        private readonly Dictionary<string, long> identities = new Dictionary<string, long>(StringComparer.Ordinal);
        internal long Count { get { return identities.Count; } }
        internal long Bytes { get; private set; }

        internal BlobSidecarWriter(string directory)
        {
            this.directory = Path.GetFullPath(directory);
            if (Directory.Exists(this.directory) || File.Exists(this.directory)) throw new IOException("Blob staging destination already exists.");
            Directory.CreateDirectory(this.directory);
        }

        internal Dictionary<string, object> Write(long length, Func<long, byte[], int, int, long> read)
        {
            if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
            if (read == null) throw new ArgumentNullException(nameof(read));
            var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                string hash;
                using (var sha = SHA256.Create())
                {
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        var buffer = new byte[BufferBytes];
                        long offset = 0;
                        while (offset < length)
                        {
                            var requested = (int)Math.Min(buffer.Length, length - offset);
                            var count = read(offset, buffer, 0, requested);
                            if (count <= 0 || count > requested) throw new InvalidDataException("Binary source returned an invalid or truncated chunk.");
                            output.Write(buffer, 0, (int)count);
                            sha.TransformBlock(buffer, 0, (int)count, buffer, 0);
                            offset += count;
                        }
                        output.Flush(true);
                    }
                    sha.TransformFinalBlock(new byte[0], 0, 0);
                    var digest = sha.Hash ?? throw new InvalidOperationException("SHA-256 did not produce a digest.");
                    hash = string.Concat(digest.Select(value => value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)));
                }
                var target = Path.Combine(directory, hash + ".bin");
                long existing;
                if (identities.TryGetValue(hash, out existing))
                {
                    if (existing != length) throw new InvalidDataException("Conflicting blob length for an existing digest.");
                    File.Delete(temporary);
                }
                else
                {
                    File.Move(temporary, target);
                    identities.Add(hash, length);
                    Bytes = checked(Bytes + length);
                }
                return new Dictionary<string, object>
                {
                    ["$binaryRef"] = new Dictionary<string, object> { ["sha256"] = hash, ["length"] = length }
                };
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        // The final JSONL is the commit marker; readers must never consume a staging file.
        internal static void Publish(string temporaryJsonl, string temporaryBlobs, string output)
        {
            var blobs = output + ".blobs";
            if (File.Exists(output) || Directory.Exists(output) || Directory.Exists(blobs) || File.Exists(blobs))
                throw new IOException("An export destination already exists; refusing to replace it.");
            Directory.Move(temporaryBlobs, blobs);
            try { File.Move(temporaryJsonl, output); }
            catch
            {
                // This directory was just moved by this call and is exporter-owned.
                Directory.Delete(blobs, true);
                throw;
            }
        }
    }
}
