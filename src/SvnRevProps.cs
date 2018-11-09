using System;
using SharpSvn;

namespace VssMigrate
{
	internal class SvnRevProps
	{
		private static readonly int _utcOffset;
		private readonly string _filePath;
	    private readonly SvnRepositoryClient _repositoryClient;
		private readonly long _revision;

	    static SvnRevProps()
		{
			_utcOffset = (int)DateTime.UtcNow.Subtract(DateTime.Now).TotalHours;
		}

        public SvnRevProps (string filePath, long revision)
        {
            _repositoryClient = new SvnRepositoryClient();
            _filePath = filePath;
            _revision = revision;
        }

		public void SetDate(DateTime date)
		{
			//2008-10-22T04:50:56.056632Z
			string dateString = date.AddHours(_utcOffset).ToString("s") + ".000000Z";
		    _repositoryClient.SetRevisionProperty(_filePath, _revision, "svn:date", dateString);

		}

		public void SetAuthor(string author)
		{
            _repositoryClient.SetRevisionProperty(_filePath, _revision, "svn:author", author);
		}
	}
}