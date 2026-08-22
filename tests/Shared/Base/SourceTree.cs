namespace Neadocs.Tests.Shared;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

public static class SourceTree
{
    private static readonly string EngineProjectMarker =
        Path.Combine("src", "Neadocs.Engine", "Neadocs.Engine.csproj");

    private static readonly Lazy<string> RootPath = new(Locate);

    public static string Root => RootPath.Value;

    public static string EngineSource => Path.Combine(Root, "src", "Neadocs.Engine");

    public static IReadOnlyList<string> EngineFiles(string extension = "*.cs") =>
        Directory
            .EnumerateFiles(EngineSource, extension, SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

    public static string RelativeToEngine(string absolutePath) =>
        Path.GetRelativePath(EngineSource, absolutePath).Replace('\\', '/');

    private static bool IsBuildOutput(string path)
    {
        string normalized = path.Replace('\\', '/');

        return normalized.Contains("/bin/") || normalized.Contains("/obj/");
    }

    private static string Locate()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, EngineProjectMarker)))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate {EngineProjectMarker} above {AppContext.BaseDirectory}. " +
            "The source-scanning guards need the repository on disk.");
    }
}
