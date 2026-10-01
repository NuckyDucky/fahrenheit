// SPDX-License-Identifier: LGPL-3.0-or-later
//
// This file is part of Fahrenheit, © 2023-2026 The Fahrenheit contributors.
// It is licensed to you under the GNU Lesser General Public License, version 3.0 or later. See COPYING, COPYING.LESSER.

using System.IO;
using System.Text.Json;

namespace Fahrenheit.Tests;

/// <summary>Local state written next to a save: what a load finds after a save was interrupted at each step.</summary>
[TestFixture]
public class FhStateSettleTests {

    private const string SAVE_A = "AAAA";
    private const string SAVE_B = "BBBB";

    private string _dir = "";

    [SetUp]
    public void SetUp() {
        _dir = Path.Join(Path.GetTempPath(), "fhtest-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown() {
        Directory.Delete(_dir, recursive: true);
    }

    private void write(string name, string text) => File.WriteAllText(Path.Join(_dir, name), text);
    private string read(string name)              => File.ReadAllText(Path.Join(_dir, name));
    private bool   exists(string name)            => File.Exists(Path.Join(_dir, name));
    private static string meta(string sha)        => $"{{\"Version\":\"1\",\"SaveSha256\":\"{sha}\"}}";

    [Test]
    public void no_leftover_changes_nothing() {
        write("state", "old");
        write("state.meta.json", meta(SAVE_A));
        Assert.That(FhState.settle_next(_dir, SAVE_A), Is.EqualTo(FhState.SettleResult.NONE));
        Assert.That(read("state"), Is.EqualTo("old"));
    }

    [Test]
    public void interrupted_before_the_save_was_written_discards_the_staged_pair() {
        write("state", "old");
        write("state.meta.json", meta(SAVE_A));
        write("state.next", "new");
        write("state.meta.next.json", meta(SAVE_B));
        Assert.That(FhState.settle_next(_dir, SAVE_A), Is.EqualTo(FhState.SettleResult.DISCARDED));
        Assert.That(read("state"), Is.EqualTo("old"));
        Assert.That(exists("state.next") || exists("state.meta.next.json"), Is.False);
    }

    [Test]
    public void interrupted_after_the_save_was_written_completes_the_staged_pair() {
        write("state", "old");
        write("state.meta.json", meta(SAVE_A));
        write("state.next", "new");
        write("state.meta.next.json", meta(SAVE_B));
        Assert.That(FhState.settle_next(_dir, SAVE_B), Is.EqualTo(FhState.SettleResult.COMPLETED));
        Assert.That(read("state"), Is.EqualTo("new"));
        Assert.That(read("state.meta.json"), Does.Contain(SAVE_B));
        Assert.That(exists("state.next") || exists("state.meta.next.json"), Is.False);
    }

    [Test]
    public void interrupted_between_the_two_moves_completes_the_meta() {
        write("state", "new");
        write("state.meta.json", meta(SAVE_A));
        write("state.meta.next.json", meta(SAVE_B));
        Assert.That(FhState.settle_next(_dir, SAVE_B), Is.EqualTo(FhState.SettleResult.COMPLETED));
        Assert.That(read("state"), Is.EqualTo("new"));
        Assert.That(read("state.meta.json"), Does.Contain(SAVE_B));
    }

    [Test]
    public void unreadable_staged_meta_is_discarded() {
        write("state", "old");
        write("state.next", "new");
        write("state.meta.next.json", "{ not json");
        Assert.That(FhState.settle_next(_dir, SAVE_B), Is.EqualTo(FhState.SettleResult.DISCARDED));
        Assert.That(read("state"), Is.EqualTo("old"));
    }

    [Test]
    public void unknown_loaded_hash_never_completes() {
        write("state.next", "new");
        write("state.meta.next.json", meta(SAVE_B));
        Assert.That(FhState.settle_next(_dir, null), Is.EqualTo(FhState.SettleResult.DISCARDED));
    }

    [Test]
    public void meta_written_before_the_hash_existed_still_reads() {
        FhLocalStateInfo? info = JsonSerializer.Deserialize<FhLocalStateInfo>("{\"Version\":\"0.4.0\"}", FhUtil.InternalJsonOpts);
        Assert.That(info, Is.Not.Null);
        Assert.That(info!.Version, Is.EqualTo("0.4.0"));
        Assert.That(info.SaveSha256, Is.Null);
    }
}
