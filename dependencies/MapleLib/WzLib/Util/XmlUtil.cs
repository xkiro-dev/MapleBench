using System;
using System.Collections;
using System.IO;
using System.Text;
using MapleLib.MapleCryptoLib;

namespace MapleLib.WzLib.Util
{
	public class XmlUtil
	{

		// CR, LF and TAB are escaped as numeric character references, not left raw.
		// They are legal inside an attribute value, but XML attribute-value
		// normalisation replaces literal ones with spaces on read-back, so a
		// MapleStory item or skill description -- which routinely contains \r\n --
		// came back from any conforming parser as a single line. .NET's own
		// XmlTextReader defaults to Normalization = false and so round-tripped them
		// intact, which is why this survived: it is lossy for every consumer of our
		// XML export except MapleLib itself.
		private static readonly char[] specialCharacters = {'"', '\'', '&', '<', '>', '\r', '\n', '\t'};
		private static readonly string[] replacementStrings = {"&quot;", "&apos;", "&amp;", "&lt;", "&gt;", "&#13;", "&#10;", "&#9;"};

		public static string SanitizeText(string text)
		{
			if (string.IsNullOrEmpty(text))
				return string.Empty;

			StringBuilder fixedText = new StringBuilder("");
			bool charFixed;
			for (int i = 0; i < text.Length; i++)
			{
				charFixed = false;
				for (int k = 0; k < specialCharacters.Length; k++)
				{

					if (text[i] == specialCharacters[k])
					{
						fixedText.Append(replacementStrings[k]);
						charFixed = true;
						break;
					}
				}
				if (!charFixed)
				{
					fixedText.Append(text[i]);
				}
			}
			return fixedText.ToString();
		}

		public static string OpenNamedTag(string tag, string name, bool finish)
		{
			return OpenNamedTag(tag, name, finish, false);
		}

		public static string EmptyNamedTag(string tag, string name)
		{
			return OpenNamedTag(tag, name, true, true);
		}

		public static string EmptyNamedValuePair(string tag, string name, string value)
		{
			return OpenNamedTag(tag, name, false, false) + Attrib("value", value, true, true);
		}

		// The name is escaped, not interpolated raw. It is attribute content exactly
		// as `value` is, and WZ node names are arbitrary user data: a name holding a
		// `"` closed the attribute and a `&` opened an undefined entity, so the
		// export was not well-formed XML and no conforming parser would read it
		// back. Nothing failed at export time -- the file was written, reported as a
		// success, and only broke later in whatever consumed it. Every named-tag
		// helper funnels through here (OpenNamedTag, EmptyNamedTag,
		// EmptyNamedValuePair), and so do WzFile/WzDirectory/WzImage.ExportXml.
		public static string OpenNamedTag(string tag, string name, bool finish, bool empty)
		{
			return "<" + tag + " name=\"" + SanitizeText(name) + "\"" + (finish ? (empty ? "/>" : ">") : " ");
		}

		public static string Attrib(string name, string value)
		{
			return Attrib(name, value, false, false);
		}

		public static string Attrib(string name, string value, bool closeTag, bool empty)
		{
			return name + "=\"" + SanitizeText(value) + "\"" + (closeTag ? (empty ? "/>" : ">") : " ");
		}

		public static string CloseTag(string tag)
		{
			return "</" + tag + ">";
		}

		public static string Indentation(int level)
		{
			char[] indent = new char[level];
			for (int i = 0; i < indent.Length; i++)
			{
				indent[i] = '\t';
			}
			return new String(indent);
		}
	}
}
