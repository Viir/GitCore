using AwesomeAssertions;
using GitCore.Common;
using System;
using System.Collections.Generic;
using System.IO;

namespace GitCore.UnitTests;

using FilePath = IReadOnlyList<string>;

public class TestData
{
    public static IReadOnlyDictionary<FilePath, ReadOnlyMemory<byte>> LoadTestDataFiles_2025_10_27()
    {
        var testDataDir =
            Path.Combine(
                AppContext.BaseDirectory,
                "TestData",
                "2025-10-27-clone",
                "files-after-git-clone");

        Directory.Exists(testDataDir).Should().BeTrue($"Missing test data at: {testDataDir}");

        var result =
            new Dictionary<FilePath, ReadOnlyMemory<byte>>(
                comparer: EnumerableExtensions.EqualityComparer<FilePath>());

        foreach (var file in Directory.EnumerateFiles(testDataDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(testDataDir, file);

            var pathParts = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var bytes = File.ReadAllBytes(file);

            result.Add(pathParts, bytes);
        }

        return result;
    }
}
