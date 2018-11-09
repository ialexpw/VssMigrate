using System;
using System.Net;

namespace VssMigrate
{
	public class SvnCredentialProvider : ICredentials
	{
		private readonly NetworkCredential _credential;

		public SvnCredentialProvider(string userName, string password)
		{
			_credential = new NetworkCredential(userName, password);
		}

		#region ICredentials Members
		public NetworkCredential GetCredential(Uri uri, string authType)
		{
			return _credential;
		}
		#endregion
	}
}