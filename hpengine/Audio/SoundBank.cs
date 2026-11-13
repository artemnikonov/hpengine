// SOUND STREAMS BY UE PATH. The path is exact (Package.Group.Name); a bare leaf name resolves only when unique
// (Core UESoundCatalog.PathForUniqueLeaf), otherwise a loud refusal. Each voice opens its own stream (as each
// PlaySoundW opens a SoundStream); the resolved path names the sample (its shared staging buffer).
using HP1.UEScript;

namespace HPEngine.Audio
{
    public sealed class SoundBank
    {
        readonly ISoundSource _src;
        readonly HashSet<string> _warned = new(StringComparer.OrdinalIgnoreCase);

        public SoundBank(ISoundSource src) { _src = src ?? throw new ArgumentNullException(nameof(src)); }

        public (ISoundStream Stream, string Path) Open(string uePathOrLeaf)
        {
            if (string.IsNullOrEmpty(uePathOrLeaf)) return (null, null);
            string path = uePathOrLeaf.IndexOf('.') >= 0 ? uePathOrLeaf : _src.PathForUniqueLeaf(uePathOrLeaf);
            if (path == null)
            {
                if (_warned.Add(uePathOrLeaf))
                    Console.Error.WriteLine($"[SoundBank] '{uePathOrLeaf}': the leaf is ambiguous or not found among the install's "
                        + "sounds — without a full path the sound is not resolved");
                return (null, null);
            }
            if (!_src.Exists(path))
            {
                if (_warned.Add(path)) Console.Error.WriteLine($"[SoundBank] '{path}': no such sound in the install packages");
                return (null, null);
            }
            return (_src.OpenStream(path), path);
        }
    }
}
