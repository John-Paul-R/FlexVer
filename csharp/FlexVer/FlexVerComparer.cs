
/*
 * To the extent possible under law, the author has dedicated all copyright
 * and related and neighboring rights to this software to the public domain
 * worldwide. This software is distributed without any warranty.
 *
 * See <http://creativecommons.org/publicdomain/zero/1.0/>
 */

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly:InternalsVisibleTo("FlexVerTests")]

namespace FlexVer;

/**
 * Implements FlexVer, a SemVer-compatible intuitive comparator for free-form versioning strings as
 * seen in the wild. It's designed to sort versions like people do, rather than attempting to force
 * conformance to a rigid and limited standard. As such, it imposes no restrictions. Comparing two
 * versions with differing formats will likely produce nonsensical results (garbage in, garbage out),
 * but best effort is made to correct for basic structural changes, and versions of differing length
 * will be parsed in a logical fashion.
 */
public static class FlexVerComparer
{
	public static IComparer<string> Default { get; } = new FlexVerComparerImpl();

	private sealed class FlexVerComparerImpl : IComparer<string>
	{
		// ReSharper disable once MemberHidesStaticFromOuterClass
		public int Compare(string? x, string? y)
		{
			if (x is null) return y is null ? 0 : -1;
			if (y is null) return 1;
			return FlexVerComparer.Compare(x, y);
		}
	}

	/// <summary>
	/// Parse the given strings as freeform version strings, and compare them according to FlexVer.
	/// </summary>
	/// <param name="a">the first version string</param>
	/// <param name="b">the second version string</param>
	/// <returns><c>0</c> if the two versions are equal, a negative number if <c>a &lt; b</c>, or a positive number if <c>a &gt; b</c></returns>
	public static int Compare(string a, string b)
	{
		if (a is null) throw new ArgumentNullException(nameof(a));
		if (b is null) throw new ArgumentNullException(nameof(b));

		var offsetA = 0;
		var offSetB = 0;
		ReadOnlySpan<char> aSpan = WithoutAppendix(a);
		ReadOnlySpan<char> bSpan = WithoutAppendix(b);
		Span<int> codepointsA = stackalloc int[32]; // 32 arbitrarily chosen
		Span<int> codepointsB = stackalloc int[32];
		while (true) {
			var ac = GetNextVersionComponent(aSpan, ref offsetA, codepointsA);
			var bc = GetNextVersionComponent(bSpan, ref offSetB, codepointsB);

			if (ac.ComponentType is VersionComponentType.Null && bc.ComponentType is VersionComponentType.Null) {
				return 0;
			}

			int c = VersionComponent.CompareTo(ac, bc);
			if (c != 0) return c;
			codepointsA.Clear();
			codepointsB.Clear();
		}
	}

	private static ReadOnlySpan<char> WithoutAppendix(string versionString)
	{
		var appendixIdx = versionString.IndexOf('+');
		return appendixIdx == -1 ? versionString : versionString.AsSpan()[..appendixIdx];
	}

	internal enum VersionComponentType
	{
		Default,
		SemVerPrerelease,
		Numeric,
		Null,
	}

	[DebuggerDisplay("{ComponentType} | '{this.ToString()}'")]
	internal ref struct VersionComponent
	{
		public ReadOnlySpan<int> Codepoints { get; }
		public VersionComponentType ComponentType { get; }

		public VersionComponent(ReadOnlySpan<int> codepoints, VersionComponentType componentType)
		{
			Codepoints = codepoints;
			ComponentType = componentType;
		}

		public static int CompareTo(VersionComponent cur, VersionComponent other)
		{
#pragma warning disable CS8524
			return cur.ComponentType switch
#pragma warning restore CS8524
			{
				VersionComponentType.Default => CompareToBase(cur, other),
				VersionComponentType.Null when other.ComponentType == VersionComponentType.Null => 0,
				VersionComponentType.Null when other.ComponentType == VersionComponentType.SemVerPrerelease => 1,
				VersionComponentType.Null => -CompareToBase(other, cur),
				VersionComponentType.Numeric => CompareToNumeric(cur, other),
				VersionComponentType.SemVerPrerelease => CompareToSemVerPrerelease(cur, other)
			};
		}

		public static int CompareToBase(VersionComponent cur, VersionComponent other)
		{
			if (other.ComponentType == VersionComponentType.Null) return 1;

			ReadOnlySpan<int> a = cur.Codepoints;
			ReadOnlySpan<int> b = other.Codepoints;

			for (int i = 0; i < Math.Min(a.Length, b.Length); i++) {
				int c1 = a[i];
				int c2 = b[i];
				if (c1 != c2) return c1 - c2;
			}

			return a.Length - b.Length;
		}

		public static int CompareToNumeric(VersionComponent cur, VersionComponent that)
		{
			if (that.ComponentType == VersionComponentType.Null) return 1;
			if (that.ComponentType == VersionComponentType.Numeric) {
				ReadOnlySpan<int> a = RemoveLeadingZeroes(cur.Codepoints);
				ReadOnlySpan<int> b = RemoveLeadingZeroes(that.Codepoints);
				if (a.Length != b.Length) return a.Length-b.Length;
				for (int i = 0; i < a.Length; i++) {
					int ad = a[i];
					int bd = b[i];
					if (ad != bd) return ad-bd;
				}
				return 0;
			}
			return CompareToBase(cur, that);
		}

		public static int CompareToSemVerPrerelease(VersionComponent left, VersionComponent right)
		{
			if (right.ComponentType == VersionComponentType.Null) return -1; // opposite order
			return CompareToBase(left, right);
		}

		private static ReadOnlySpan<int> RemoveLeadingZeroes(ReadOnlySpan<int> a)
		{
			if (a.Length == 1) return a;
			int i = 0;
			while (i < a.Length && a[i] == '0') {
				i++;
			}
			return a[i..];
		}

		public override string ToString() => new(MemoryMarshal.Cast<int, char>(Codepoints));
	}

	internal static VersionComponent GetNextVersionComponent(
		ReadOnlySpan<char> span,
		ref int i,
		Span<int> writableComponentCodepoints)
	{
		if (span.Length == i) {
			return new VersionComponent(ReadOnlySpan<int>.Empty, VersionComponentType.Null);
		}

		bool lastWasNumber = char.IsAsciiDigit(span[i]);

		ValueListBuilder<int> builder = new ValueListBuilder<int>(writableComponentCodepoints);

		int currentComponentCharCount = 0;
		while (i < span.Length) {
			char cp = span[i];
			if (char.IsHighSurrogate(cp)) i++;

			bool isNumber = char.IsAsciiDigit(cp);
			if (// Ending a Number component
				isNumber != lastWasNumber
				// Starting a new PreRelease component
			    || (cp == '-' && currentComponentCharCount > 0 && builder[0] != '-')
			) {
				return CreateComponent(lastWasNumber, builder.AsSpan(), currentComponentCharCount);
			}
			currentComponentCharCount++;
			builder.Append(cp);
			i++;
		}
		return CreateComponent(lastWasNumber, builder.AsSpan(), currentComponentCharCount);
	}

	private static VersionComponent CreateComponent(bool number, ReadOnlySpan<int> s, int j)
	{
		s = s[..j];

		if (number) {
			return new VersionComponent(s, VersionComponentType.Numeric);
		}

		if (s.Length > 1 && s[0] == '-') {
			return new VersionComponent(s, VersionComponentType.SemVerPrerelease);
		}

		return new VersionComponent(s, VersionComponentType.Default);
	}

}
