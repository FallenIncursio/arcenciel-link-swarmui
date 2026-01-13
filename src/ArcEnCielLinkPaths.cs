using System.IO;
using System.Text.RegularExpressions;
using SwarmUI.Core;
using SwarmUI.Text2Image;

namespace ArcEnCiel.Link.Swarm;

internal static class ArcEnCielLinkPaths
{
    private static readonly Regex SafeSegment = new("^[A-Za-z0-9 _.,#@!$%^&()\\-+=\\u00A0-\\u024F]+$", RegexOptions.Compiled);
    private static readonly HashSet<string> ModelFileExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".safetensors",
        ".ckpt",
        ".pt",
        ".sft",
        ".gguf"
    };

    public static IEnumerable<string> GetModelRoots()
    {
        foreach (string key in new[] { "Stable-Diffusion", "LoRA", "VAE", "Embedding" })
        {
            if (!Program.T2IModelSets.TryGetValue(key, out T2IModelHandler? handler))
            {
                continue;
            }

            foreach (string root in handler.FolderPaths ?? [])
            {
                if (!string.IsNullOrWhiteSpace(root))
                {
                    yield return root;
                }
            }
        }
    }

    public static bool IsModelFile(string path)
    {
        string ext = Path.GetExtension(path);
        return ModelFileExts.Contains(ext);
    }

    public static string ResolveTargetPath(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            throw new ArgumentException("Target path is required.");
        }

        string normalized = targetPath.Replace('\\', '/').Trim().Trim('/');
        if (normalized.Length == 0 || normalized.StartsWith("../", StringComparison.Ordinal))
        {
            throw new ArgumentException("Target path is invalid.");
        }

        string[] parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            throw new ArgumentException("Target path is invalid.");
        }

        int offset = 0;
        string first = parts[0].ToLowerInvariant();
        T2IModelHandler handler;
        if (first == "embeddings" || first == "embedding")
        {
            handler = GetHandler("embedding");
            offset = 1;
        }
        else if (first == "models")
        {
            if (parts.Length < 2)
            {
                throw new ArgumentException("Target path must include a model category.");
            }

            string category = parts[1].ToLowerInvariant();
            handler = GetHandler(category);
            offset = 2;
        }
        else
        {
            throw new ArgumentException("Target path must start with embeddings or models.");
        }

        string root = handler.DownloadFolderPath;
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("Target path folder is unavailable.");
        }

        string combined = root;
        for (int i = offset; i < parts.Length; i++)
        {
            string segment = parts[i];
            EnsureSafeSegment(segment);
            combined = Path.Combine(combined, segment);
        }

        string full = Path.GetFullPath(combined);
        string baseFull = Path.GetFullPath(root);
        if (!full.StartsWith(baseFull, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Target path escapes allowed directories.");
        }

        return full;
    }

    public static IReadOnlyList<string> ListSubfolders(string kind)
    {
        T2IModelHandler handler = GetHandler(kind);
        HashSet<string> results = new(StringComparer.OrdinalIgnoreCase);

        foreach (string root in handler.FolderPaths ?? [])
        {
            if (!Directory.Exists(root))
            {
                continue;
            }
            try
            {
                foreach (string dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
                {
                    string name = Path.GetFileName(dir);
                    if (string.IsNullOrWhiteSpace(name) || name.StartsWith('.'))
                    {
                        continue;
                    }

                    string relative = Path.GetRelativePath(root, dir).Replace('\\', '/');
                    if (string.IsNullOrWhiteSpace(relative) || relative.StartsWith('.'))
                    {
                        continue;
                    }

                    if (relative.Split('/').Any(segment => segment.StartsWith('.')))
                    {
                        continue;
                    }

                    results.Add(relative);
                }
            }
            catch
            {
                continue;
            }
        }

        return results.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void EnsureSafeSegment(string segment)
    {
        if (segment is "." or "..")
        {
            throw new ArgumentException("Target path contains traversal segments.");
        }

        if (!SafeSegment.IsMatch(segment))
        {
            throw new ArgumentException("Target path contains unsupported characters.");
        }
    }

    private static T2IModelHandler GetHandler(string kind)
    {
        string key = kind.ToLowerInvariant();
        string handlerKey = key switch
        {
            "checkpoint" or "checkpoints" or "stable-diffusion" or "stable_diffusion" => "Stable-Diffusion",
            "lora" => "LoRA",
            "vae" or "vaes" => "VAE",
            "embedding" or "embeddings" or "emb" => "Embedding",
            _ => throw new ArgumentException("Unsupported model category.")
        };

        if (!Program.T2IModelSets.TryGetValue(handlerKey, out T2IModelHandler? handler))
        {
            throw new ArgumentException("Model handler unavailable.");
        }

        return handler;
    }
}
