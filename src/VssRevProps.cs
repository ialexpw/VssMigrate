using System;

namespace VssMigrate
{
	/// <summary>
	/// Used as a key for determining which revision each file should be a part of
	/// The key is composed of the author, time and comment
	/// </summary>
	internal class VssRevProps : IComparable
	{
		public string Comment { get; set; }
		public string Author { get; set; }
		public DateTime Time { get; set; }

		public override string ToString()
		{
			return string.Format("{0:yyyyMMdd HHmmss} by {1} - {2}", Time, Author, Comment);
		}

		public int CompareTo(object obj)
		{
			if (obj == null)
				throw new ArgumentNullException("Cannot compare to a null object");
			if (!(obj is VssRevProps))
				throw new ArgumentException("Can only compare object of type VssRevProps to type VssRevProps");
			var p = (VssRevProps)obj;
			if (p.Time == Time)
			{
			    if (string.Compare(p.Author, Author, true) == 0 && string.Compare(p.Comment, Comment) == 0)
					return 0;
			    
                return string.Compare(p.Author, Author, true);
			}
		    if (Time <= p.Time)
				return -1;
			if (Time > p.Time)
				return 1;

			return -1;
		}
	}
}
