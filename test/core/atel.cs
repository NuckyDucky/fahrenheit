// SPDX-License-Identifier: LGPL-3.0-or-later
//
// This file is part of Fahrenheit, © 2023-2026 The Fahrenheit contributors.
// It is licensed to you under the GNU Lesser General Public License, version 3.0 or later. See COPYING, COPYING.LESSER.

using Fahrenheit.Atel;

namespace Fahrenheit.Tests;

[TestFixture]
public unsafe class FhAtelTests {

    /* The script header offset table after the 0x38-byte chunk header holds one u32 per script.
     * Values below are the first three entries of FF X's bjyt0000.ebp; read as u16 they came out as 324, 0, 376.
     */
    [Test]
    public void script_header_offsets_are_u32() {
        byte[] chunk = new byte[0x38 + 3 * sizeof(uint)];
        BitConverter.TryWriteBytes(chunk.AsSpan(0x34), (ushort)3);
        BitConverter.TryWriteBytes(chunk.AsSpan(0x38), 324u);
        BitConverter.TryWriteBytes(chunk.AsSpan(0x3C), 376u);
        BitConverter.TryWriteBytes(chunk.AsSpan(0x40), 428u);

        fixed (byte* p = chunk) {
            AtelScriptChunk* c = (AtelScriptChunk*)p;
            Assert.That(c->script_num,               Is.EqualTo(3));
            Assert.That(c->script_header_offsets[0], Is.EqualTo(324u));
            Assert.That(c->script_header_offsets[1], Is.EqualTo(376u));
            Assert.That(c->script_header_offsets[2], Is.EqualTo(428u));
        }
    }

    // The game's float pop (FFX.exe 0x86DDE0) loads an F32 entry with `fld` and an I32 entry with `fild`; neither truncates.
    [Test]
    public void pop_float_keeps_fraction() {
        AtelStack stack = new();
        stack.push_float(1.5f);
        Assert.That(stack.pop_float(), Is.EqualTo(1.5f));
    }

    [Test]
    public void pop_float_converts_int() {
        AtelStack stack = new();
        stack.push_int(7);
        Assert.That(stack.pop_float(), Is.EqualTo(7f));
    }
}
