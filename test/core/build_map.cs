// SPDX-License-Identifier: LGPL-3.0-or-later
//
// This file is part of Fahrenheit, © 2023-2026 The Fahrenheit contributors.
// It is licensed to you under the GNU Lesser General Public License, version 3.0 or later. See COPYING, COPYING.LESSER.

using System.IO;

namespace Fahrenheit.Tests;

[TestFixture]
public class FhBuildMapTests {

    private const string SAMPLE = """
        # address-map v1
        # module FFX.exe
        # canonical 55D2F3CC 3d13e5ed53821df8db204cd3c27d470c5c220b18e16849fc4440996c15f277e1 753362
        # build 6AA2219C 0537b2a1047f3266e73495cd4e35f63f0777f4231d417699f979954686da686d 25501027
        00000000 00000000 00001000 hdr -
        00001000 00001000 00000100 fn 558BEC83EC105356
        00002000 00001F00 00000010 sub 8B4D0833C0894104
        00002010 00001F20 00000020 sub 5DC3CCCCCCCCCCCC
        00800000 00800000 00100000 data -
        """;

    private static FhAddressMap parse_ok(string text) {
        Assert.That(FhAddressMap.try_parse(text, out FhAddressMap? map, out string error), Is.True, error);
        return map!;
    }

    [Test]
    public void parse_reads_header_and_segments() {
        FhAddressMap map = parse_ok(SAMPLE);

        Assert.That(map.Module,             Is.EqualTo("FFX.exe"));
        Assert.That(map.BuildTimestamp,     Is.EqualTo(0x6AA2219Cu));
        Assert.That(map.CanonicalTimestamp, Is.EqualTo(0x55D2F3CCu));
        Assert.That(map.BuildId,            Is.EqualTo("25501027"));
        Assert.That(map.Segments.Length,    Is.EqualTo(5));
        Assert.That(map.Segments[2].kind,   Is.EqualTo(FhAddressSegmentKind.sub));
    }

    [Test]
    public void translate_inside_segments() {
        FhAddressMap map = parse_ok(SAMPLE);

        Assert.That(map.try_translate(0x1000, out nint t0), Is.True);  Assert.That(t0, Is.EqualTo((nint)0x1000));
        Assert.That(map.try_translate(0x2004, out nint t1), Is.True);  Assert.That(t1, Is.EqualTo((nint)0x1F04));
        Assert.That(map.try_translate(0x200F, out nint t2), Is.True);  Assert.That(t2, Is.EqualTo((nint)0x1F0F));
        Assert.That(map.try_translate(0x2010, out nint t3), Is.True);  Assert.That(t3, Is.EqualTo((nint)0x1F20));
        Assert.That(map.try_translate(0x202F, out nint t4), Is.True);  Assert.That(t4, Is.EqualTo((nint)0x1F3F));
        Assert.That(map.try_translate(0x8FFFFF, out nint t5), Is.True); Assert.That(t5, Is.EqualTo((nint)0x8FFFFF));
    }

    [Test]
    public void translate_outside_segments_is_identity() {
        FhAddressMap map = parse_ok(SAMPLE);

        foreach (nint o in (nint[])[ 0x1100, 0x1FFF, 0x2030, 0x7FFFFF, 0x900000, 0x7FFFFFFF ]) {
            Assert.That(map.try_translate(o, out nint t), Is.False, $"0x{o:X}");
            Assert.That(t, Is.EqualTo(o));
        }
    }

    [Test]
    public void expected_bytes_only_at_fn_and_sub_starts() {
        FhAddressMap map = parse_ok(SAMPLE);

        Assert.That(map.try_expected_bytes(0x1000, out ulong e0), Is.True);
        Assert.That(FhAddressMap.format_bytes(e0), Is.EqualTo("558BEC83EC105356"));
        Assert.That(map.try_expected_bytes(0x1F20, out ulong e1), Is.True);
        Assert.That(FhAddressMap.format_bytes(e1), Is.EqualTo("5DC3CCCCCCCCCCCC"));

        Assert.That(map.try_expected_bytes(0x1F04,   out _), Is.False); // mid-segment
        Assert.That(map.try_expected_bytes(0x0,      out _), Is.False); // hdr
        Assert.That(map.try_expected_bytes(0x800000, out _), Is.False); // data
    }

    [TestCase("00001000 00001000 00000100 fn 558BEC83EC105356\n",                                     TestName = "missing format tag")]
    [TestCase("# address-map v1\n# build 6AA2219C x y\n",                                             TestName = "missing module")]
    [TestCase("# address-map v1\n# module FFX.exe\n",                                                 TestName = "missing build")]
    [TestCase("# address-map v1\n# module FFX.exe\n# build 6AA2219C x y\n1000 1000 100 fn 558BEC83EC105356\n", TestName = "short hex")]
    [TestCase("# address-map v1\n# module FFX.exe\n# build 6AA2219C x y\n00001000 00001000 00000100 fn -\n",    TestName = "fn without expect8")]
    [TestCase("# address-map v1\n# module FFX.exe\n# build 6AA2219C x y\n00001000 00001000 00000100 data 558BEC83EC105356\n", TestName = "data with expect8")]
    [TestCase("# address-map v1\n# module FFX.exe\n# build 6AA2219C x y\n00001000 00001000 00000100 code -\n",  TestName = "bad kind")]
    [TestCase("# address-map v1\n# module FFX.exe\n# build 6AA2219C x y\n00001000 00001000 00000000 data -\n",  TestName = "zero length")]
    [TestCase("# address-map v1\n# module FFX.exe\n# build 6AA2219C x y\n00002000 00002000 00000010 data -\n00001000 00001000 00000010 data -\n", TestName = "unsorted")]
    [TestCase("# address-map v1\n# module FFX.exe\n# build 6AA2219C x y\n00001000 00001000 00000100 data -\n00001080 00002000 00000010 data -\n", TestName = "overlap")]
    [TestCase("# address-map v1\n# module FFX.exe\n# build 6AA2219C x y\n00001000 00001000 00000100 data\n",    TestName = "four fields")]
    public void malformed_map_is_rejected(string text) {
        Assert.That(FhAddressMap.try_parse(text, out _, out string error), Is.False);
        Assert.That(error, Is.Not.Empty);
    }

    [Test]
    public void canonical_build_is_identity_without_a_map() {
        string dir = Path.Join(Path.GetTempPath(), $"fh-build-map-{Guid.NewGuid():N}");

        Assert.That(FhBuildMap.resolve("FFX.exe",   FhBuildMap.FFX_CANONICAL_TIMESTAMP,  dir, out FhAddressMap? m0, out _, out _), Is.EqualTo(FhBuildState.Canonical));
        Assert.That(FhBuildMap.resolve("FFX-2.exe", FhBuildMap.FFX2_CANONICAL_TIMESTAMP, dir, out FhAddressMap? m1, out _, out _), Is.EqualTo(FhBuildState.Canonical));
        Assert.That(m0, Is.Null);
        Assert.That(m1, Is.Null);

        Assert.That(FhBuildMap.translate_for("FFX.exe",   FhBuildMap.FFX_CANONICAL_TIMESTAMP,  0x39B500), Is.EqualTo((nint)0x39B500));
        Assert.That(FhBuildMap.translate_for("FFX-2.exe", FhBuildMap.FFX2_CANONICAL_TIMESTAMP, 0x123456), Is.EqualTo((nint)0x123456));

        // The running process is not a game: the main-module fast path is identity.
        Assert.That(FhBuildMap.translate(0x39B500),            Is.EqualTo((nint)0x39B500));
        Assert.That(FhBuildMap.translate("FFX.exe", 0x39B500), Is.EqualTo((nint)0x39B500));
    }

    [Test]
    public void resolve_states_from_map_directory() {
        string dir = Directory.CreateTempSubdirectory("fh-build-map-").FullName;
        try {
            Assert.That(FhBuildMap.resolve("FFX.exe", 0x6AA2219C, dir, out _, out string p0, out string e0), Is.EqualTo(FhBuildState.Unsupported));
            Assert.That(Path.GetFileName(p0), Is.EqualTo("FFX.exe-6AA2219C.map"));
            Assert.That(e0, Is.EqualTo("no map file"));

            File.WriteAllText(p0, SAMPLE);
            Assert.That(FhBuildMap.resolve("FFX.exe", 0x6AA2219C, dir, out FhAddressMap? m1, out _, out _), Is.EqualTo(FhBuildState.Mapped));
            Assert.That(m1!.Segments.Length, Is.EqualTo(5));

            File.WriteAllText(p0, SAMPLE.Replace("00002010 00001F20", "00002008 00001F20"));
            Assert.That(FhBuildMap.resolve("FFX.exe", 0x6AA2219C, dir, out FhAddressMap? m2, out _, out string e2), Is.EqualTo(FhBuildState.Unsupported));
            Assert.That(m2, Is.Null);
            Assert.That(e2, Does.StartWith("malformed map"));

            // A map whose header names another build is not used.
            File.WriteAllText(p0, SAMPLE.Replace("# build 6AA2219C", "# build 6AA2219D"));
            Assert.That(FhBuildMap.resolve("FFX.exe", 0x6AA2219C, dir, out _, out _, out _), Is.EqualTo(FhBuildState.Unsupported));
        }
        finally {
            Directory.Delete(dir, true);
        }
    }

    /* Round trip on the real Steam builds: MsSetRamChr at canonical offset 0x39B500 (build 753362)
     * sits at 0x39B4F0 in build 25501027 (24-byte window identical). Needs the archived executables
     * from the overhaul repo's .local/ folder; skipped when they are not there.
     */
    [Test]
    public void real_build_pair_msSetRamChr_round_trip() {
        string? root = find_up(".local");
        if (root is null) Assert.Ignore("no .local/game-build-* executables next to this checkout");

        string old_exe = Path.Join(root, "game-build-753362",   "FFX.exe");
        string new_exe = Path.Join(root, "game-build-25501027", "FFX.exe");
        if (!File.Exists(old_exe) || !File.Exists(new_exe)) Assert.Ignore("archived FFX.exe pair missing");

        byte[] old_bytes = File.ReadAllBytes(old_exe);
        byte[] new_bytes = File.ReadAllBytes(new_exe);

        const uint CANON = 0x39B500;
        const uint MOVED = 0x39B4F0;

        string text = "# address-map v1\n# module FFX.exe\n# canonical 55D2F3CC - 753362\n# build 6AA2219C - 25501027\n"
                    + $"{CANON:X8} {MOVED:X8} 00000018 fn 558BEC83EC105356\n";
        FhAddressMap map = parse_ok(text);

        Assert.That(map.try_translate((nint)CANON, out nint t), Is.True);
        Assert.That(t, Is.EqualTo((nint)MOVED));

        ReadOnlySpan<byte> old_win = old_bytes.AsSpan(rva_to_file(old_bytes, CANON), 24);
        ReadOnlySpan<byte> new_win = new_bytes.AsSpan(rva_to_file(new_bytes, (uint)t), 24);
        Assert.That(new_win.SequenceEqual(old_win), Is.True, $"{Convert.ToHexString(old_win)} vs {Convert.ToHexString(new_win)}");

        Assert.That(map.try_expected_bytes(t, out ulong expect8), Is.True);
        Assert.That(System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(new_win), Is.EqualTo(expect8));
    }

    private static string? find_up(string name) {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent) {
            string candidate = Path.Join(d.FullName, name);
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static int rva_to_file(byte[] pe, uint rva) {
        int lfanew   = BitConverter.ToInt32(pe, 0x3C);
        int sections = BitConverter.ToUInt16(pe, lfanew + 6);
        int opt_size = BitConverter.ToUInt16(pe, lfanew + 20);
        int table    = lfanew + 24 + opt_size;

        for (int i = 0; i < sections; i++) {
            int  s     = table + 40 * i;
            uint vsize = BitConverter.ToUInt32(pe, s + 8);
            uint va    = BitConverter.ToUInt32(pe, s + 12);
            uint rsize = BitConverter.ToUInt32(pe, s + 16);
            uint raw   = BitConverter.ToUInt32(pe, s + 20);
            if (rva >= va && rva < va + Math.Max(vsize, rsize)) return (int)(raw + (rva - va));
        }
        throw new ArgumentOutOfRangeException(nameof(rva));
    }
}
