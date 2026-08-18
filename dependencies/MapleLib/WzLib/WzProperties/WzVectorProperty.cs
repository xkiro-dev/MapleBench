using System.IO;
using MapleLib.WzLib.Util;

namespace MapleLib.WzLib.WzProperties
{
	/// <summary>
	/// A property that contains an x and a y value
	/// </summary>
	public class WzVectorProperty : WzExtended
	{
		#region Fields
		internal string name;
		internal WzIntProperty x, y;
		internal WzObject parent;
		//internal WzImage imgParent;
		#endregion

		#region Inherited Members
        public override void SetValue(object value)
        {
            if (value is System.Drawing.Point)
            {
                x.val = ((System.Drawing.Point)value).X;
                y.val = ((System.Drawing.Point)value).Y;
            }
            else
            {
                x.val = ((System.Drawing.Size)value).Width;
                y.val = ((System.Drawing.Size)value).Height;
            }
        }

        public override WzImageProperty DeepClone()
        {
            // The X and Y are cloned, not shared. Passing them to the
            // (string, WzIntProperty, WzIntProperty) constructor assigns the
            // references straight across, so the "clone" and the original were
            // one node wearing two names: SetValue writes through x.val/y.val, so
            // editing a duplicated or copied vector rewrote the original too --
            // across images, and without marking the original's image changed, so
            // the tree and the file disagreed with nothing saying so.
            WzVectorProperty clone = new WzVectorProperty(name);
            if (x != null)
                clone.X = new WzIntProperty(x.Name, x.Value);
            if (y != null)
                clone.Y = new WzIntProperty(y.Name, y.Value);
            return clone;
        }

		public override object WzValue { get { return new System.Drawing.Point(x.Value, y.Value); } }
		/// <summary>
		/// The parent of the object
		/// </summary>
		public override WzObject Parent { get { return parent; } internal set { parent = value; } }
		/*/// <summary>
		/// The image that this property is contained in
		/// </summary>
		public override WzImage ParentImage { get { return imgParent; } internal set { imgParent = value; } }*/
		/// <summary>
		/// The name of the property
		/// </summary>
		public override string Name { get { return name; } set { name = value; } }
		/// <summary>
		/// The WzPropertyType of the property
		/// </summary>
		public override WzPropertyType PropertyType { get { return WzPropertyType.Vector; } }
		public override void WriteValue(WzBinaryWriter writer)
		{
			writer.WriteStringValue("Shape2D#Vector2D", WzImage.WzImageHeaderByte_WithoutOffset, WzImage.WzImageHeaderByte_WithOffset);
			writer.WriteCompressedInt(X.Value);
			writer.WriteCompressedInt(Y.Value);
		}
		public override void ExportXml(StreamWriter writer, int level)
		{
			writer.WriteLine(XmlUtil.Indentation(level) + XmlUtil.OpenNamedTag("WzVector", this.Name, false, false) +
				XmlUtil.Attrib("X", this.X.Value.ToString()) + XmlUtil.Attrib("Y", this.Y.Value.ToString(), true, true));
		}
		/// <summary>
		/// Disposes the object
		/// </summary>
		/// <remarks>
		/// Null-guarded, because a vector with a missing half is reachable and a
		/// throw here is not survivable in the way an ordinary throw is: the one
		/// caller is the recursive dispose walk that closes an archive, and an
		/// exception part way through it leaves half a tree torn down, the
		/// session's resolution cache still pointing into it, and the close
		/// itself reported as a failure.
		///
		/// Both halves are read separately when a vector is parsed
		/// (<c>WzImageProperty.ParseExtendedProp</c> builds it with neither set
		/// and assigns X and then Y), and <see cref="DeepClone"/> copies each
		/// only when it is present — so "X but no Y" is a shape this library
		/// produces itself. Disposing twice used to be fatal for the same
		/// reason: the second pass found the nulls this one left behind.
		/// </remarks>
		public override void Dispose()
		{
			name = null;
			x?.Dispose();
			x = null;
			y?.Dispose();
			y = null;
		}
		#endregion

		#region Custom Members
		/// <summary>
		/// The X value of the Vector2D
		/// </summary>
		public WzIntProperty X { get { return x; } set { x = value; } }
		/// <summary>
		/// The Y value of the Vector2D
		/// </summary>
		public WzIntProperty Y { get { return y; } set { y = value; } }
		/// <summary>
		/// The Point of the Vector2D created from the X and Y
		/// </summary>
		public System.Drawing.Point Pos { get { return new System.Drawing.Point(X.Value, Y.Value); } }
		/// <summary>
		/// Creates a blank WzVectorProperty
		/// </summary>
		public WzVectorProperty() { }
		/// <summary>
		/// Creates a WzVectorProperty with the specified name
		/// </summary>
		/// <param name="name">The name of the property</param>
		public WzVectorProperty(string name)
		{
			this.name = name;
		}
		/// <summary>
		/// Creates a WzVectorProperty with the specified name, x and y
		/// </summary>
		/// <param name="name">The name of the property</param>
		/// <param name="x">The x value of the vector</param>
		/// <param name="y">The y value of the vector</param>
		public WzVectorProperty(string name, WzIntProperty x, WzIntProperty y)
		{
			this.name = name;
			this.x = x;
			this.y = y;
		}

		/// <summary>
		/// Creates a WzVectorProperty with the specified name, x and y
		/// </summary>
		/// <param name="name">The name of the property</param>
		/// <param name="x">The x value of the vector</param>
		/// <param name="y">The y value of the vector</param>
		public WzVectorProperty(string name, int x, int y)
		{
			this.name = name;
			this.x = new WzIntProperty(string.Empty, x);
			this.y = new WzIntProperty(string.Empty, y);
		}

		/// <summary>
		/// Creates a WzVectorProperty with the specified name, x and y
		/// </summary>
		/// <param name="name">The name of the property</param>
		/// <param name="x">The x value of the vector</param>
		/// <param name="y">The y value of the vector</param>
		public WzVectorProperty(string name, float x, float y)
		{
			this.name = name;
			this.x = new WzIntProperty(string.Empty, (int)x);
			this.y = new WzIntProperty(string.Empty, (int)y);
		}
		#endregion

		#region Cast Values
		public override System.Drawing.Point GetPoint()
        {
            return new System.Drawing.Point(x.val, y.val);
        }

        public override string ToString()
        {
            return "X: " + x.val.ToString() + ", Y: " + y.val.ToString();
        }
        #endregion
	}
}