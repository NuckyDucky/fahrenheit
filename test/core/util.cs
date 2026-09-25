// SPDX-License-Identifier: LGPL-3.0-or-later
//
// This file is part of Fahrenheit, © 2023-2026 The Fahrenheit contributors.
// It is licensed to you under the GNU Lesser General Public License, version 3.0 or later. See COPYING, COPYING.LESSER.

using System.Numerics;

namespace Fahrenheit.Tests;

[TestFixture]
public class FhUtilTests {

    [Test]
    public void as_rgba_packs_each_channel() {
        Assert.That(new Vector4(1f, 0f, 0f, 0f).as_rgba(), Is.EqualTo(0x000000FFu));
        Assert.That(new Vector4(0f, 1f, 0f, 1f).as_rgba(), Is.EqualTo(0xFF00FF00u));
        Assert.That(new Vector4(1f, 1f, 1f, 1f).as_rgba(), Is.EqualTo(0xFFFFFFFFu));
    }
}
