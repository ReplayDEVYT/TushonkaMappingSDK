using System.Collections.Generic;
using System.IO;
using System.Linq;
using EFT;
using EFT.Interactive;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace TarkovSDK.Editor
{
    // The rustle of trees and bushes. TreeInteractive plays its _soundBank while a player moves through its trigger
    // spheres (louder with speed and turning), and every retail tree and bush (78,597 across the game's scenes: pines,
    // filbert, fern, brush_dry, hedges) points at one bank, "TreeInteractive" in sharedassets5.assets: 16 branches_*
    // clips, BaseVolume 0.5, DeltaVolume 0.1, DeltaPitch 0.05, Rolloff 55, Environment group. That file only loads with
    // retail scenes, so on an SDK map a TreeInteractive without its own bank stays silent.
    //
    // Bank() copies that bank out of the game install (its clips as .wav under Assets/TarkovGameAssets, like the Asset
    // Importer's sounds, never committed); without the game it makes the SDK's own (generated rustles, same values).
    // Build Map gives it to every TreeInteractive that has none (Prepare).
    public static class TarkovFoliageSounds
    {
        private const string GameFile = "sharedassets5.assets";
        private const string BankName = "TreeInteractive";
        private const string SdkFolder = TarkovTrees.AssetFolder + "/Sounds";
        private const string SdkBankPath = SdkFolder + "/SDK_Foliage.asset";

        private static string GameFolder => Path.GetDirectoryName(GameAssetStore.PathOf(GameFile, "SoundBanks", BankName, 0, ".asset")).Replace('\\', '/');

        // the bank the SDK's trees and bushes use: the game's when it can be read, else the SDK's own
        public static SoundBank Bank()
        {
            var game = ExistingGameBank();
            if (game != null)
                return game;
            if (TarkovGameData.Available)
            {
                game = ImportGameBank(out var error);
                if (game != null)
                    return game;
                Debug.LogWarning($"[Tarkov SDK] Foliage sounds: the game's tree rustle didn't import ({error}); using the SDK's own");
            }
            return SdkBank();
        }

        // what Bank() would give, without making anything (for the Tree Builder's label)
        public static string Describe()
        {
            if (ExistingGameBank() != null)
                return "the game's tree rustle (imported)";
            if (TarkovGameData.Available)
                return "the game's tree rustle (imported on first use)";
            return "the SDK's own rustle (no game install found)";
        }

        private static SoundBank ExistingGameBank()
        {
            if (!AssetDatabase.IsValidFolder(GameFolder))
                return null;
            return AssetDatabase.FindAssets("t:SoundBank", new[] { GameFolder })
                .Select(g => AssetDatabase.LoadAssetAtPath<SoundBank>(AssetDatabase.GUIDToAssetPath(g)))
                .FirstOrDefault(b => b != null && b.name.StartsWith(BankName));
        }

        // ------------------------------------------------------------ the game's bank

        private static SoundBank ImportGameBank(out string error)
        {
            error = null;
            var files = TarkovGameData.Files;
            var file = files.Get(GameFile);
            if (file == null)
            {
                error = $"{GameFile} not found";
                return null;
            }
            long bankId = 0;
            foreach (var info in file.ObjectList)
            {
                if (info.ClassId != 114 || !file.ScriptHeader(info.PathId, out _, out _, out var script))
                    continue;
                var name = files.Script(file, script).Name;
                if (name == null || !name.EndsWith("SoundBank"))
                    continue;
                file.ReadScript<bool>(info.PathId, out var header, null);
                if (header?.Str("m_Name") == BankName)
                {
                    bankId = info.PathId;
                    break;
                }
            }
            if (bankId == 0)
            {
                error = $"no {BankName} bank in {GameFile}";
                return null;
            }

            var bank = ScriptableObject.CreateInstance<SoundBank>();
            var failed = new List<string>();
            // the clips are only noted while the bank reads: importing one reads the same file, which would move the
            // reader's place in it. They come in the order the arrays are walked, so they're filled in that order after.
            var clipRefs = new List<GamePPtr>();
            var reader = new GameScriptReader
            {
                Resolve = (pptr, type) =>
                {
                    if (type == typeof(AudioClip))
                        clipRefs.Add(pptr);
                    return null;
                },
            };
            if (!file.ReadScript(bankId, out _, (r, size) => reader.Read(r, size, typeof(SoundBank), bank)))
            {
                error = reader.Error ?? "it didn't read";
                Object.DestroyImmediate(bank);
                return null;
            }
            int next = 0;
            foreach (var distance in bank.Environments?.SelectMany(e => e.Clips ?? new DistanceVarity[0]) ?? Enumerable.Empty<DistanceVarity>())
            {
                var clips = new List<AudioClip>();
                for (int i = 0; i < (distance.Clips?.Length ?? 0) && next < clipRefs.Count; i++)
                {
                    // clips that didn't decode are left out rather than kept as empty slots
                    var clip = Clip(files, file, clipRefs[next++], failed);
                    if (clip != null)
                        clips.Add(clip);
                }
                distance.Clips = clips.ToArray();
            }
            if (!Clips(bank).Any())
            {
                error = "none of its clips decoded" + (failed.Count > 0 ? ": " + failed[0] : "");
                Object.DestroyImmediate(bank);
                return null;
            }
            bank.name = BankName;
            var path = GameAssetStore.PathOf(GameFile, "SoundBanks", BankName, bankId, ".asset");
            TarkovGeneratedAssets.EnsureFolder(GameFolder);
            AssetDatabase.CreateAsset(bank, path);
            AssetDatabase.SaveAssets();
            Debug.Log($"[Tarkov SDK] Foliage sounds: the game's tree rustle imported to {path} ({Clips(bank).Count()} clips" +
                      (failed.Count > 0 ? $", {failed.Count} didn't decode" : "") + ")");
            return bank;
        }

        private static AudioClip Clip(GameFiles files, GameSerializedFile from, GamePPtr pptr, List<string> failed)
        {
            if (!files.Resolve(from, pptr, out var file, out var info) || info.ClassId != 83)
                return null;
            var name = file.Read(info.PathId, "m_Name")?.Str("m_Name") ?? "clip";
            var entry = new GameIndexEntry
            {
                Category = "Sounds",
                Name = name,
                Asset = file.Name,
                AssetId = info.PathId,
                Signature = $"Sounds|{file.Name}:{info.PathId}",
            };
            var clip = TarkovGameSounds.Import(entry, out var error);
            if (clip == null)
                failed.Add($"{name}: {error}");
            return clip;
        }

        private static IEnumerable<AudioClip> Clips(SoundBank bank) =>
            bank.Environments?.Where(e => e?.Clips != null).SelectMany(e => e.Clips).Where(d => d?.Clips != null).SelectMany(d => d.Clips).Where(c => c != null)
            ?? Enumerable.Empty<AudioClip>();

        // ------------------------------------------------------------ the SDK's own bank

        // the retail bank's values over generated rustles: bursts of leaf and twig noise, 1.2-2.4 s like branches_*
        private static SoundBank SdkBank()
        {
            var existing = AssetDatabase.LoadAssetAtPath<SoundBank>(SdkBankPath);
            if (existing != null && Clips(existing).Any())
                return existing;
            TarkovGeneratedAssets.EnsureFolder(SdkFolder);
            var clips = new List<AudioClip>();
            for (int i = 0; i < 8; i++)
            {
                var path = $"{SdkFolder}/SDK_Rustle_{i + 1:00}.wav";
                if (AssetDatabase.LoadAssetAtPath<AudioClip>(path) == null)
                {
                    WriteWav(path, Rustle(i));
                    AssetDatabase.ImportAsset(path);
                }
                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                if (clip != null)
                    clips.Add(clip);
            }
            var bank = existing != null ? existing : ScriptableObject.CreateInstance<SoundBank>();
            bank.BaseVolume = 0.5f;
            bank.BasePitch = 1f;
            bank.DeltaVolume = 0.1f;
            bank.DeltaPitch = 0.05f;
            bank.Rolloff = 55f;
            bank.SourceType = BetterAudio.AudioSourceGroupType.Environment;
            bank._clipLiength = clips.Count > 0 ? clips.Max(c => c.length) : 0f;
            // [environment][distance]: TreeInteractive plays outdoor (environment 0), the nearest distance
            bank.Environments = new EnvironmentVariety[2];
            for (int e = 0; e < 2; e++)
            {
                bank.Environments[e] = new EnvironmentVariety { Clips = new DistanceVarity[3] };
                for (int d = 0; d < 3; d++)
                    bank.Environments[e].Clips[d] = new DistanceVarity { Clips = e == 0 && d == 0 ? clips.ToArray() : new AudioClip[0] };
            }
            if (existing == null)
            {
                bank.name = "SDK_Foliage";
                AssetDatabase.CreateAsset(bank, SdkBankPath);
            }
            else
                EditorUtility.SetDirty(bank);
            AssetDatabase.SaveAssets();
            return bank;
        }

        // stereo 44.1 kHz: a swell of brushing noise with leaf and twig crackles in it
        private static float[,] Rustle(int seed)
        {
            const int rate = 44100;
            var random = new System.Random(9157 + seed * 31);
            float seconds = 1.2f + (float)random.NextDouble() * 1.2f;
            int frames = (int)(seconds * rate);
            var samples = new float[frames, 2];
            // a few swells: leaves dragging past the body
            var swells = Enumerable.Range(0, 2 + random.Next(3))
                .Select(_ => (Centre: (float)random.NextDouble() * seconds, Width: 0.15f + (float)random.NextDouble() * 0.35f, Gain: 0.4f + (float)random.NextDouble() * 0.6f)).ToList();
            // crackles: short bright clicks of twigs and dry leaves
            var crackles = Enumerable.Range(0, 30 + random.Next(40)).Select(_ => (float)random.NextDouble() * seconds).OrderBy(t => t).ToArray();
            for (int c = 0; c < 2; c++)
            {
                float low = 0f, high = 0f, previous = 0f;
                int next = 0;
                float crackle = 0f;
                for (int i = 0; i < frames; i++)
                {
                    float t = i / (float)rate;
                    float white = (float)random.NextDouble() * 2f - 1f;
                    // band-passed noise, roughly 600 Hz to 6 kHz: the hiss of leaves
                    low += (white - low) * 0.55f;
                    high += (low - high) * 0.08f;
                    float band = low - high;
                    float envelope = 0f;
                    foreach (var s in swells)
                    {
                        float x = (t - s.Centre) / s.Width;
                        envelope += s.Gain * Mathf.Exp(-x * x);
                    }
                    while (next < crackles.Length && crackles[next] <= t)
                    {
                        // each channel hears each crackle a little differently, which gives width
                        crackle = Mathf.Max(crackle, 0.4f + (float)random.NextDouble() * 0.6f);
                        next++;
                    }
                    float click = crackle * (white - previous) * 0.5f;
                    crackle *= 0.9985f;
                    previous = white;
                    float fade = Mathf.Clamp01(t / 0.03f) * Mathf.Clamp01((seconds - t) / 0.08f);
                    samples[i, c] = Mathf.Clamp((band * envelope * 0.9f + click * (0.25f + envelope * 0.4f)) * fade, -1f, 1f);
                }
            }
            return samples;
        }

        private static void WriteWav(string path, float[,] samples)
        {
            const int rate = 44100;
            int frames = samples.GetLength(0), channels = samples.GetLength(1);
            int dataSize = frames * channels * 2;
            using (var w = new BinaryWriter(File.Create(path)))
            {
                w.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
                w.Write(36 + dataSize);
                w.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E', (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
                w.Write(16);
                w.Write((short)1);
                w.Write((short)channels);
                w.Write(rate);
                w.Write(rate * channels * 2);
                w.Write((short)(channels * 2));
                w.Write((short)16);
                w.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
                w.Write(dataSize);
                for (int i = 0; i < frames; i++)
                    for (int c = 0; c < channels; c++)
                        w.Write((short)Mathf.Clamp(Mathf.RoundToInt(samples[i, c] * 32767f), -32768, 32767));
            }
        }

        // ------------------------------------------------------------ Build Map

        // every TreeInteractive without a bank gets the foliage bank (the game's own trees ripped from retail scenes lose
        // theirs too: AssetRipper keeps the reference to a file the map's bundles don't carry)
        public static void Prepare(Scene scene, GameObject[] roots, TarkovScenePreparer.Report report)
        {
            var silent = roots.SelectMany(r => r.GetComponentsInChildren<TreeInteractive>(true)).Where(t => t._soundBank == null).ToList();
            if (silent.Count == 0)
                return;
            var bank = Bank();
            if (bank == null)
            {
                report.Warnings.Add($"{scene.name}: {silent.Count} tree(s)/bush(es) have no rustle sound and no foliage bank could be made");
                return;
            }
            foreach (var tree in silent)
                tree._soundBank = bank;
            Debug.Log($"[Tarkov SDK] {scene.name}: rustle sound ({bank.name}) given to {silent.Count} tree(s)/bush(es)");
        }
    }
}
