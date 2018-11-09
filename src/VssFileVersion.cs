using SourceSafeTypeLib;

namespace VssMigrate
{
	/// <summary>
	/// Used as the holder of a pointer to the actual item in VSS as well as caching some of the metadata
	/// </summary>
	internal class VssFileVersion
	{
		public string Spec { get; set; }
		public int VersionNumber { get; set; }
		public IVSSVersion Version { get; set; }
        public string Action { get; set; }
        public bool Deleted { get; set; }

		public override string ToString()
		{
			return string.Format("{0}:{1}", Spec, VersionNumber);
		}
	}
}
