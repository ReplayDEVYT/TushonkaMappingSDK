using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TarkovSDK.Editor
{
    // The Asset Importer's sounds: a game AudioClip is loaded through a one-clip asset bundle made around it
    // (GameAudio.Bundle; Unity decodes FMOD's Vorbis, which nothing else here can), played with the editor's preview
    // player and written into the project as a 16-bit .wav under Assets/TarkovGameAssets/<file>/Sounds, with the
    // game's load type and compression on its importer.
    [InitializeOnLoad]
    public static class TarkovGameSounds
    {
        private const string Prefix = "CAB-tarkovsdk-";
        private static AssetBundle _bundle;
        private static AudioClip _clip;
        private static string _key;

        static TarkovGameSounds()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Release;
            EditorApplication.quitting += Release;
        }

        private static string KeyOf(GameIndexEntry e) => e.Asset + ":" + e.AssetId;

        // the entry's clip, decoded (kept until another is loaded or Release)
        public static AudioClip Load(GameIndexEntry entry, out string error)
        {
            error = null;
            if (_clip != null && _key == KeyOf(entry))
                return _clip;
            Release();
            var files = TarkovGameData.Files;
            var file = files?.Get(entry.Asset);
            var sound = file != null ? GameAudio.Read(files, file, entry.AssetId) : null;
            if (sound?.Bank == null)
            {
                error = "its sound data didn't read";
                return null;
            }
            var bytes = GameAudio.Bundle(sound, Prefix + Guid.NewGuid().ToString("N"));
            _bundle = bytes != null ? AssetBundle.LoadFromMemory(bytes) : null;
            if (_bundle == null)
            {
                error = "Unity didn't load it (see the Console)";
                return null;
            }
            _clip = _bundle.LoadAsset<AudioClip>(GameAudio.AssetPath(sound));
            if (_clip == null || _clip.loadState != AudioDataLoadState.Loaded)
            {
                error = $"Unity didn't decode its {sound.Codec}";
                Release();
                return null;
            }
            _clip.hideFlags = HideFlags.HideAndDontSave;
            _key = KeyOf(entry);
            return _clip;
        }

        public static void Release()
        {
            Stop();
            if (_bundle != null)
                _bundle.Unload(true);
            _bundle = null;
            _clip = null;
            _key = null;
            // a bundle left from before a script reload still holds its name; free it so the next load can't clash
            foreach (var bundle in AssetBundle.GetAllLoadedAssetBundles())
                if (bundle != null && bundle.name.StartsWith(Prefix))
                    bundle.Unload(true);
        }

        // ---- the editor's preview player (UnityEditor.AudioUtil, internal)

        private static readonly Type AudioUtil = typeof(AudioImporter).Assembly.GetType("UnityEditor.AudioUtil");

        private static object Call(string method, params object[] args)
        {
            var m = AudioUtil?.GetMethod(method, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (m == null)
                return null;
            var parameters = m.GetParameters();
            var full = new object[parameters.Length];
            for (int i = 0; i < full.Length; i++)
                full[i] = i < args.Length ? args[i] : parameters[i].DefaultValue;
            return m.Invoke(null, full);
        }

        public static bool Play(GameIndexEntry entry, out string error)
        {
            var clip = Load(entry, out error);
            if (clip == null)
                return false;
            Stop();
            Call("PlayPreviewClip", clip, 0, false);
            return true;
        }

        public static void Stop() => Call("StopAllPreviewClips");

        public static bool IsPlaying(GameIndexEntry entry) =>
            _clip != null && _key == KeyOf(entry) && Call("IsPreviewClipPlaying") is bool playing && playing;

        // ---- import

        public static string AssetPathOf(GameIndexEntry entry) => GameAssetStore.PathOf(entry.Asset, "Sounds", entry.Name, entry.AssetId, ".wav");

        // the entry as a .wav in the project (made once, then reused)
        public static AudioClip Import(GameIndexEntry entry, out string error)
        {
            error = null;
            var path = AssetPathOf(entry);
            var existing = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            if (existing != null)
                return existing;
            var clip = Load(entry, out error);
            if (clip == null)
                return null;
            TarkovGeneratedAssets.EnsureFolder(Path.GetDirectoryName(path).Replace('\\', '/'));
            if (!WriteWav(clip, path))
            {
                File.Delete(path);
                error = "its samples didn't read";
                return null;
            }
            AssetDatabase.ImportAsset(path);
            var importer = AssetImporter.GetAtPath(path) as AudioImporter;
            var sound = GameAudio.Read(TarkovGameData.Files, TarkovGameData.Files.Get(entry.Asset), entry.AssetId);
            if (importer != null && sound != null)
            {
                // the game's own settings for it
                var settings = importer.defaultSampleSettings;
                settings.loadType = (AudioClipLoadType)Mathf.Clamp(sound.LoadType, 0, 2);
                settings.compressionFormat = sound.Compression == 1 ? AudioCompressionFormat.Vorbis : sound.Compression == 2 ? AudioCompressionFormat.ADPCM : AudioCompressionFormat.PCM;
                importer.defaultSampleSettings = settings;
                importer.userData = "TarkovGame:" + entry.Signature;
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<AudioClip>(path);
        }

        // 16-bit PCM, interleaved, a few seconds at a time (the game's radio has half-hour podcasts)
        private static bool WriteWav(AudioClip clip, string path)
        {
            int channels = clip.channels, frames = clip.samples;
            long dataSize = (long)frames * channels * 2;
            if (dataSize > int.MaxValue - 44)
                return false;
            var chunk = new float[Mathf.Min(frames, 1 << 18) * channels];
            using (var w = new BinaryWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16)))
            {
                w.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
                w.Write((int)(36 + dataSize));
                w.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E', (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
                w.Write(16);
                w.Write((short)1);
                w.Write((short)channels);
                w.Write(clip.frequency);
                w.Write(clip.frequency * channels * 2);
                w.Write((short)(channels * 2));
                w.Write((short)16);
                w.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
                w.Write((int)dataSize);
                var bytes = new byte[chunk.Length * 2];
                for (int at = 0; at < frames; at += chunk.Length / channels)
                {
                    int count = Mathf.Min(chunk.Length / channels, frames - at) * channels;
                    if (count < chunk.Length)
                        chunk = new float[count];
                    if (!clip.GetData(chunk, at))
                        return false;
                    for (int i = 0; i < count; i++)
                    {
                        short s = (short)Mathf.Clamp(Mathf.RoundToInt(chunk[i] * 32767f), -32768, 32767);
                        bytes[i * 2] = (byte)s;
                        bytes[i * 2 + 1] = (byte)(s >> 8);
                    }
                    w.Write(bytes, 0, count * 2);
                }
            }
            return true;
        }

        // a sound source playing the clip at the point, set up like the SDK's Ambient Sound Point (looped if long)
        public static GameObject Place(AudioClip clip, Vector3 point)
        {
            var go = new GameObject(clip.name);
            go.transform.position = point;
            var source = go.AddComponent<AudioSource>();
            source.clip = clip;
            source.loop = clip.length >= 5f;
            source.playOnAwake = true;
            source.spatialBlend = 1f;
            source.rolloffMode = AudioRolloffMode.Logarithmic;
            source.minDistance = 2f;
            source.maxDistance = 50f;
            source.dopplerLevel = 0f;
            source.volume = 0.6f;
            TarkovAudioSources.SetUp(source, undo: false);
            Undo.RegisterCreatedObjectUndo(go, "Place " + clip.name);
            return go;
        }
    }
}
