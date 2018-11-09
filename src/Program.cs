using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Configuration;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web;
using log4net;
using log4net.Config;
using SharpSvn;
using SharpSvn.Security;
using SourceSafeTypeLib;

namespace VssMigrate
{
    internal static class Program
    {
        #region Variables

        private static readonly SortedList<VssRevProps, Dictionary<string, VssFileVersion>> revisions =
            new SortedList<VssRevProps, Dictionary<string, VssFileVersion>>();

        private static readonly List<IVSSItem> fileList = new List<IVSSItem>();
/*
        private static readonly List<IVSSItem> projList = new List<IVSSItem>();
*/
        private static readonly List<string> labellist = new List<string>();
        private static readonly List<string> tagslist = new List<string>();

        private static readonly ILog generalLog = LogManager.GetLogger("General");
        private static readonly ILog mergeLog = LogManager.GetLogger("Merge");
        private static readonly ILog migrateLog = LogManager.GetLogger("Migrate");
        private static readonly ILog searchLog = LogManager.GetLogger("Search");
        
        private static int MergeRevisionWindow;

        private static int nRetCode;
        private static int numFilesHandled;
        //private static int numRevisionsHandled;
        private static string outputDIR;
        private static bool PerformImport;
        private static string repoDIR;
        private static string svnBRANCH;
        private static string svnPASSWORD;
        private static string svnPROJ;
        private static string svnREVPROPSPATH;
        private static string svnTAG;
        private static string svnURL;
        private static string svnUSER;
        private static string TagSourceUrl;

        private static VSSDatabase vssDb;
        private static string vssDIR;
        private static Regex vssFileExclusionRegex;
        private static Regex vssFileInclusionRegex;
        private static Regex vssFolderExclusionRegex;
        private static Regex vssFolderInclusionRegex;
        private static string vssPASSWORD;
        private static string vssPROJ;
        private static string vssSRCSAFEINI;
        private static string vssUSER;

        private static readonly VssRevProps pinProps = new VssRevProps
        {
            Author = "Pinner",
            Comment =
                "Pin Revision overriding latest versions of files with pinned ones.",
            Time = DateTime.UtcNow
        };

        private static bool IgnoreExceptions;
        private static bool trunkexists;

        #endregion

        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        private static void Main()
        {
            XmlConfigurator.Configure(new FileInfo("log4net.config"));
            nRetCode = 0;

            DateTime startTime = DateTime.Now;

            //check command lines
            if (!ReadProperties())
            {
                generalLog.Fatal("Bad/missing info in VssMigrate.exe.config file.");
                Environment.Exit(1);
            }

            Cleanup();

            try
            {
                if (!BuildFileList())
                {
                    ExitError();
                }

                //Output a simple xml file in order to review the results of the revisions
                //This should be rewritten in order to better utilize the XML components of .net, but this was quick enough...
                using (var fs = new StreamWriter("revisions.xml", false, Encoding.ASCII))
                {
                    fs.WriteLine(string.Format("<revisions count=\"{0}\">", revisions.Keys.Count));
                    foreach (VssRevProps key in revisions.Keys)
                    {
                        fs.WriteLine(string.Format("\t<revision time=\"{0}\" author=\"{1}\" comment=\"{2}\">",
                                                   key.Time, key.Author, HttpUtility.HtmlEncode(key.Comment)));
                        foreach (string key2 in revisions[key].Keys)
                            fs.WriteLine(
                                string.Format(
                                    "\t\t<file name=\"{0}\" version=\"{1}\" label=\"{2}\" action=\"{3}\"/>",
                                    revisions[key][key2].Spec, revisions[key][key2].VersionNumber,
                                    revisions[key][key2].Version.Label, revisions[key][key2].Version.Action));

                        fs.WriteLine("\t</revision>");
                    }
                    fs.WriteLine("</revisions>");
                }

                if (!MergeRevisions())
                {
                    ExitError();
                }

                //Output a simple xml file containing final merged revisions
                //This should be rewritten in order to better utilize the XML components of .net, but this was quick enough...
                using (var fs = new StreamWriter("revisions_merged.xml", false, Encoding.ASCII))
                {
                    fs.WriteLine(string.Format("<revisions count=\"{0}\">", revisions.Keys.Count));
                    foreach (VssRevProps key in revisions.Keys)
                    {
                        fs.WriteLine(string.Format("\t<revision time=\"{0}\" author=\"{1}\" comment=\"{2}\">",
                                                   key.Time, key.Author, HttpUtility.HtmlEncode(key.Comment)));
                        foreach (string key2 in revisions[key].Keys)
                            fs.WriteLine(
                                string.Format(
                                    "\t\t<file name=\"{0}\" version=\"{1}\" label=\"{2}\" action=\"{3}\"/>",
                                    revisions[key][key2].Spec, revisions[key][key2].VersionNumber,
                                    revisions[key][key2].Version.Label, revisions[key][key2].Version.Action));

                        fs.WriteLine("\t</revision>");
                    }
                    fs.WriteLine("</revisions>");
                }

                if (!PerformImport)
                {
                    generalLog.Info("PerformImport setting is false; stopping execution before performing import");
                    generalLog.Info("Please review revisions_merged.xml to see the projected migration");

                    generalLog.InfoFormat("Start time: {0}", startTime);
                    generalLog.InfoFormat("End time: {0}", DateTime.Now);
                    Environment.Exit(0);
                }

                if (!ImportDirectories())
                {
                    ExitError();
                }

                GetAndAddRevisions();

                generalLog.InfoFormat("Start time: {0}", startTime);
                generalLog.InfoFormat("End time: {0}", DateTime.Now);
                generalLog.Info("External commands run: 0");
                generalLog.InfoFormat("Files Migrated: {0}", numFilesHandled);
                //generalLog.InfoFormat("File Revisions Migrated: {0}", numRevisionsHandled);
            }
            catch (Exception ex)
            {
                generalLog.Fatal(string.Empty, ex);
                Console.ReadKey();
            }
        }

        private static void Cleanup() 
        {
            generalLog.DebugFormat("Cleaning up folder {0}", repoDIR);
            //subversion files (.svn directory) are read-only and other files may be read-only which 
            //will cause the Directory.Delete to throw an UnauthorizedAccessException so the workaround
            //is to reset the attributes of all files in the folder to normal first
            for (int i = 0; i <= 3; i++)
            {
                try
                {
                    if (Directory.Exists(repoDIR))
                    {

                        string[] files = Directory.GetFiles(repoDIR, "*", SearchOption.AllDirectories);
                        foreach (string file in files)
                        {
                            File.SetAttributes(file, FileAttributes.Normal);
                            File.Delete(file);
                        }
                        Directory.Delete(repoDIR, true);
                    }
                }
                catch (Exception)
                {
                    if (i == 3)
                    {
                        throw;
                    }
                    continue;
                }
            }
        }

        private static SvnClient GetSvnClient()
        {
            var client = new SvnClient();
            client.Authentication.DefaultCredentials = new SvnCredentialProvider(svnUSER, svnPASSWORD);
            client.Authentication.SslServerTrustHandlers +=
                delegate(object o, SvnSslServerTrustEventArgs e)
                    {
                        //accept wrong ssl certificates automatically
                        e.AcceptedFailures = e.Failures;
                        e.Save = true; // Save acceptance to authentication store
                    };
            return client;
        }

        private static void ExitError()
        {
            generalLog.Info("Exiting.  Working directory will be cleaned up in ten seconds...");
            Thread.Sleep(10000);
            Cleanup();

            Environment.Exit(nRetCode);
        }

        private static string sanitizeLabel(String Label)
        {
            string tmpString = Label.Replace(" ", "_");
            string tmpString2 = tmpString.Replace("/", "_");
            string tmpString3 = tmpString2.Replace("#", "Nr");
            string finishedString = tmpString3.Trim();

            return finishedString;
        }

        private static string GenerateSourceUrl(IVSSItem vssFile)
        {
            if (vssFile.Parent.Spec != vssPROJ && !(vssPROJ.Contains(vssFile.Spec)))
            {
                TagSourceUrl = String.Format("{0}/{1}", GenerateSourceUrl(vssFile.Parent), vssFile.Name);
            }
            else
            {
                if (vssPROJ.Contains(vssFile.Spec))
                {
                    if (svnPROJ.EndsWith("trunk/") || svnPROJ.EndsWith("trunk"))
                    {
                        TagSourceUrl = String.Format("{0}/{1}", svnURL, svnPROJ);
                    }
                    else
                    {
                        TagSourceUrl = String.Format("{0}/{1}/trunk", svnURL, svnPROJ);
                    }
                }
                else
                {
                    if (svnPROJ.EndsWith("trunk/") || svnPROJ.EndsWith("trunk"))
                    {
                        TagSourceUrl = String.Format("{0}/{1}/{2}/{3}", svnURL, svnPROJ,
                                                     TagSourceUrl, vssFile.Name);
                    }
                    else
                    {
                        TagSourceUrl = String.Format("{0}/{1}/trunk/{2}/{3}", svnURL, svnPROJ,
                                                     TagSourceUrl, vssFile.Name);
                    }
                }
            }

            return TagSourceUrl;
        }

        private static void CounterfeitRevProps(SvnClient svnClient, SvnTarget target, IVSSVersion vssVersion)
        {
            SvnInfoEventArgs infoEventArgs;
            svnClient.GetInfo(target, out infoEventArgs);

            var props = new SvnRevProps(svnREVPROPSPATH, infoEventArgs.Revision);

            if (vssVersion != null)
            {
                props.SetAuthor(vssVersion.Username);
                props.SetDate(vssVersion.Date);
            }
        }

        private static void ApplyTag(IVSSItem vssFile, SvnClient svnClient, IVSSVersion vssVersion, Tag tag)
        {
            var copyArgs = new SvnCopyArgs
                               {
                                   LogMessage =
                                       string.Format("Released {0}\n{1}", vssVersion.Label, vssVersion.LabelComment)
                               };

            migrateLog.DebugFormat("------------------------------------");
            migrateLog.DebugFormat(string.Format("Setting Tag:"));
            migrateLog.DebugFormat(string.Format("Label: {0}", vssVersion.Label));
            migrateLog.DebugFormat(string.Format("Comment: {0}", vssVersion.LabelComment));
            migrateLog.DebugFormat(string.Format("from Url: {0}", tag.fromUrlString));
            migrateLog.DebugFormat(string.Format("to Url: {0}", tag.tagString));


            try
            {
                svnClient.RemoteCopy(SvnTarget.FromString(tag.fromUrlString),
                                     new Uri(tag.tagString),
                                     copyArgs);

                CounterfeitRevProps(svnClient, SvnTarget.FromUri(new Uri(tag.tagString)), vssVersion);
            }


            catch (SvnFileSystemException e)
            {
                //tags with same names get handled here
                
                    if (e.ToString().Contains("already exists"))
                    {
                        int index = tag.tagString.IndexOf(svnTAG) + svnTAG.Length + 1;
                        //FIXME: correct calculation of // and /
                        string newstring = tag.tagString.Insert(index, vssFile.Parent.Name + "_");
                        tag.tagString = newstring;
                        migrateLog.DebugFormat("Tag already existing, adding parent name ...");

                        ApplyTag(vssFile, svnClient, vssVersion, tag);

                        return;
                    }

                    migrateLog.ErrorFormat("Error: Line was: " + tag.fromUrlString);
                    migrateLog.ErrorFormat(e.ToString());
                    migrateLog.InfoFormat(String.Format("Fallback, tagging repository successful: "
                                                        + (svnClient.RemoteCopy(
                                                            SvnTarget.FromString(String.Format("{0}/{1}", svnURL,
                                                                                               svnPROJ)),
                                                            new Uri(tag.tagString)
                                                            , copyArgs))));


                    CounterfeitRevProps(svnClient, SvnTarget.FromUri(new Uri(tag.tagString)), vssVersion);
                

            }

            catch (Exception e)
            {
                migrateLog.ErrorFormat(e.ToString());
                //Console.Out.WriteLine("Press a key to continue");
                //Console.ReadKey();
            }

            migrateLog.DebugFormat("------------------------------------");
        }

        #region BuildFileList and Helpers

        private static bool BuildFileList()
        {
            searchLog.Info("##### Building file list");

            vssDb = new VSSDatabase();
            vssDb.Open(vssSRCSAFEINI, vssUSER, vssPASSWORD);
            VSSItem vssRootItem = vssDb.get_VSSItem(vssPROJ, false);
            if (vssRootItem.Type == (int) VSSItemType.VSSITEM_PROJECT)
            {
                searchLog.Debug(vssRootItem.Spec);
                searchLog.Debug(vssRootItem.Parent.Spec);
                //Directory.CreateDirectory(Path.Combine(repoDIR, vssRootItem.Parent.Spec.Substring(2).Replace("/", "\\")));
                fileList.Add(vssRootItem);
                BuildFileList(vssRootItem);
            }
            else
            {
                fileList.Add(vssRootItem);
            }

            if (fileList.Count == 0)
            {
                searchLog.Error("No results from building file list");
                return false;
            }

            return true;
        }

       private static void BuildFileList(IVSSItem fromVssProject)
        {
            string spec = fromVssProject.Spec;
            if (vssFolderExclusionRegex != null && vssFolderExclusionRegex.IsMatch(spec))
            {
                searchLog.DebugFormat("Skipping project [Matched folder exclusion regex] {0}", spec);
                return;
            }
            if (vssFolderInclusionRegex != null && !vssFolderInclusionRegex.IsMatch(spec))
            {
                searchLog.DebugFormat("Skipping file [Failed folder inclusion regex] {0}", spec);
                return;
            }
            //still used to generate the project/directories at the beginning of the migration
            /*if (!projList.Exists(proj => string.Compare(proj.Spec, spec, true) == 0))
            {
                projList.Add(fromVssProject);
            }*/

            IVSSItems childItems = fromVssProject.get_Items(true);
            IEnumerator enumerator = childItems.GetEnumerator();
            while (enumerator.MoveNext())
            {
                var childItem = (IVSSItem) enumerator.Current;
                if (childItem != null)
                    if (childItem.Type == (int) VSSItemType.VSSITEM_PROJECT)
                    {
                        searchLog.InfoFormat("Scanning Project {0}", childItem.Spec);
                        BuildRevisionList(childItem);
                        BuildFileList(childItem);
                    }
                    else
                    {
                        // skip VSS metadata files.
                        if (!Path.GetExtension(childItem.Name).Contains("scc"))
                        {
                            string fileSpec = childItem.Spec;
                            if (vssFileExclusionRegex != null && vssFileExclusionRegex.IsMatch(fileSpec))
                            {
                                searchLog.DebugFormat("Skipping file [Matched file exclusion regex] {0}", fileSpec);
                                continue;
                            }
                            if (vssFileInclusionRegex != null && !vssFileInclusionRegex.IsMatch(fileSpec))
                            {
                                searchLog.DebugFormat("Skipping file [Failed file inclusion regex] {0}", fileSpec);
                                continue;
                            }
                            //fileList still used for internal checks
                            fileList.Add(childItem);
                            BuildRevisionList(childItem);
                        }
                    }
            }
        }

        private static void BuildRevisionList(IVSSItem item)
        {
            IVSSVersions versions = item.get_Versions(0);
            foreach (IVSSVersion version in versions)
            {
                try
                {
                    if (version.VSSItem.Deleted)
                    {
                        if (version == versions[versions.Count - 1])
                        {
                            AddRevision(version, true);
                        }
                    }

                    /*if (item.Type == (int) VSSItemType.VSSITEM_PROJECT)
                    {
                        if (projList.Exists(proj => string.Compare(proj.Spec, item.Spec, true) == 0))
                        {
                            //migrateLog.Debug(item.Spec + " already exists!");
                            continue;
                        }
                        projList.Add(item);
                    }*/

                    AddRevision(version, false);
                }
                catch (COMException ex)
                {
                    switch ((uint) ex.ErrorCode)
                    {
                        case 0x80040000: //version is corrupted and unavailable
                            searchLog.WarnFormat(
                                "Skipping version due to corruption {0} in file {1} [cannot read resource]",
                                version.VersionNumber, item.Spec);
                            continue;
                        case 0x8004D68F: //file not found
                            searchLog.WarnFormat("Skipping version due to corruption {0} in file {1} [file not found]",
                                                 version.VersionNumber, item.Spec);
                            continue;
                        default:
                            throw;
                    }
                }
            }
        }

        private static void AddRevision(IVSSVersion version, bool deleted)
        {
            var key = new VssRevProps
                          {
                              Author = version.Username,
                              Comment = version.Comment.Replace("\n", "").Replace("\t", "").Trim(),
                              Time = version.Date
                          };
            var file = new VssFileVersion
                           {
                               Spec = version.VSSItem.Spec,
                               VersionNumber = version.VersionNumber,
                               Version = version,
                               Action = version.Action,
                               Deleted = deleted
                           };

            //Ignore branches and the root directory
            if (version.Action.Contains("Branched at version") || 
                version.Action.ToLower().Contains("verzweigt bei version") ||
                string.Compare("$/", file.Spec, true) == 0)
            {
                return;
            }

            //if pinned add to a special (and always last) revision
            if (version.VSSItem.IsPinned)
            {
                if (!revisions.ContainsKey(pinProps))  //if there is no pinprop revision, add one ...
                {
                    revisions.Add(pinProps, new Dictionary<string, VssFileVersion>(StringComparer.CurrentCultureIgnoreCase) { { file.Spec, file } });
                }
                else //else add file to pinprop revision
                {
                    revisions[pinProps].Add(file.Spec, file);
                }

            }       
            

            //filter duplicate labels
            if (!string.IsNullOrEmpty(version.Label))
            {
                if (labellist.Contains(version.VSSItem.Spec))
                {
                    if (tagslist.Contains(version.Label))
                    {
                        return;
                    }
                    tagslist.Add(version.Label);
                }
                else
                {
                    labellist.Add(version.VSSItem.Spec);
                    tagslist.Add(version.Label);
                }
            }

            //if revision is the same but file was not added yet, then ...
            if (revisions.Keys.Contains(key) && (!revisions[key].ContainsKey(file.Spec)))// || version.VSSItem.Type == (int) VSSItemType.VSSITEM_PROJECT))
            {
                //Add the file to the existing revision if it exists
                searchLog.DebugFormat("Adding {0}:{1} to {2}", file.Spec, file.VersionNumber, key);
                revisions[key].Add(file.Spec, file);
                return;
            }

            if (revisions.Keys.Contains(key) && (revisions[key].ContainsKey(file.Spec))) {return;}

            //else revision has not been added yet, so ...
            var files = new Dictionary<string, VssFileVersion>(StringComparer.CurrentCultureIgnoreCase)
                            {{file.Spec, file}};
            searchLog.DebugFormat("New revision {0} {1}:{2}", key, file.Spec, file.VersionNumber);
            revisions.Add(key, files);
            return;
        }

        /// <summary>
        /// This handles merging the revisions in the event that a single check-in spans a window of time rather than an exact
        /// second in time.
        /// </summary>
        /// <returns></returns>
        private static bool MergeRevisions()
        {
            //Merge from the back of the list to move items forward
            //This could potentially merge everything into a single revision with a time of the
            //first revision if there are no comments and every version is within the mergedrevision_seconds of each other
            //however, a single person is not likely to be making that many changes on a regular basis
            if (revisions.Count <= 1 || MergeRevisionWindow == 0)
            {
                mergeLog.WarnFormat("Skipping revision merge because there is nothing to do");
                return true;
            }
            mergeLog.InfoFormat("Starting to merge revisions with count of {0}", revisions.Count);
            //No point in checking the last revision with nothing
            //Start with the most recent files and move backward through the list
            //By moving the files to an earlier point in time (if within the window), we can keep moving them to an earlier
            //point in time thereby allowing more revisions to get merged as the time of the earliest revision
            for (int i = revisions.Keys.Count - 2; i >= 0; i--)
            {
                int j = i;
                VssRevProps key = revisions.Keys[i];
                while (++j < revisions.Keys.Count &&
                       revisions.Keys[j].Time.Subtract(key.Time).TotalSeconds <= MergeRevisionWindow)
                {
                    //Make sure we don't have any duplicate files before checking the author and/or comments.
                    //If we have a duplicate file, we can't even proceed with merging to the past
                    bool bDuplicate = false;
                    //the i is "later" (older) than the j which is "newer" (more recent)
                    foreach (VssFileVersion laterfile in revisions[key].Values)
                    {
                        foreach (VssFileVersion newerfile in revisions[revisions.Keys[j]].Values)
                        {
                            if (string.Compare(laterfile.Spec, newerfile.Spec, true) == 0)
                            {
                                bDuplicate = true;
                                break;
                            }
                        }
                        if (bDuplicate)
                        {
                            break;
                        }
                    }
                    //We must stop if we have encountered a duplicate file because we cannot merge anything beyond this point
                    if (bDuplicate)
                    {
                        break;
                    }
                    //No duplicates; now make sure we have other matching properties for the revision
                    if (string.Compare(key.Author, revisions.Keys[j].Author, true) == 0 &&
                        string.Compare(key.Comment, revisions.Keys[j].Comment, true) == 0)
                    {
                        mergeLog.DebugFormat("Merging revision {0}\n\tinto\t{1}", revisions.Keys[j], key);
                        //Merge the more recent revisions into the older revision
                        foreach (var laterfile in revisions[revisions.Keys[j]])
                            revisions[key].Add(laterfile.Key, laterfile.Value);
                        revisions.Remove(revisions.Keys[j]);
                        break;
                    }
                }
            }
            mergeLog.InfoFormat("Merge completed with a final revision count of {0}", revisions.Count);
            return true;
        }

        #endregion

        #region Import Directories

        private static bool ImportDirectories()
        {
            var uri = new Uri(string.Format("{0}{1}", svnURL, svnPROJ));
            string dir = Path.Combine(repoDIR, vssPROJ.Substring(2).Replace("/", "\\"));

            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            
            using (SvnClient svnClient = GetSvnClient())
            {
                var importArgs = new SvnImportArgs
                                     {LogMessage = string.Format("Initial import from VSS on {0}", DateTime.Now)};
                
                SvnInfoEventArgs infoEventArgs;
                try
                {
                    trunkexists = svnClient.GetInfo(SvnTarget.FromUri(uri), out infoEventArgs);
                    migrateLog.DebugFormat(svnClient.GetInfo(SvnTarget.FromUri(uri), out infoEventArgs) ? 
                        "Getting trunk revision was successful." : 
                        "Getting trunk revision was NOT successful!");

                    migrateLog.DebugFormat("Base Revision: " + infoEventArgs.Revision);

                    if (infoEventArgs.Revision == 0)
                    {
                        if (!svnClient.Import(dir, uri, importArgs))
                        {
                            return false;
                        }
                    }
                }
                catch (Exception)
                {
                    if (!svnClient.Import(dir, uri, importArgs))
                    {
                        return false;
                    }
                }

                var tagurl = new Uri(String.Format("{1}/{0}", svnTAG, svnURL));
                var branchurl = new Uri(String.Format("{1}/{0}", svnBRANCH, svnURL));

                try
                {
                    svnClient.GetInfo(tagurl, out infoEventArgs);
                    svnClient.GetInfo(branchurl, out infoEventArgs);
                    migrateLog.DebugFormat(svnClient.GetInfo(tagurl, out infoEventArgs)?
                    "Getting tag dir revision was successful." : 
                    "Getting tag dir revision was NOT successful!");

                    migrateLog.DebugFormat(svnClient.GetInfo(branchurl, out infoEventArgs)?
                    "Getting branch dir revision was successful." : 
                    "Getting branch dir revision was NOT successful!");

                }
                catch (SvnRepositoryIOException)
                {
                    var tagdircreated = svnClient.RemoteCreateDirectory((tagurl), new SvnCreateDirectoryArgs
                                                                                         {
                                                                                             LogMessage = "Initial creation of tag directory", CreateParents = true
                                                                                         });
                    var branchdircreated = svnClient.RemoteCreateDirectory((branchurl),
                                                    new SvnCreateDirectoryArgs
                                                        {LogMessage = "Initial creation of branch directory", CreateParents = true});
                }

                try
                {
                    svnClient.GetInfo(uri, out infoEventArgs);
                }
                catch (SvnRepositoryIOException)
                {
                    svnClient.RemoteCreateDirectory((uri),
                                                    new SvnCreateDirectoryArgs
                                                        {LogMessage = "Initial import", CreateParents = true});
                }


                migrateLog.DebugFormat(dir);
                //Update the author and time of the first import revision to correspond to the first file revision
                //minus a minute in time for proper ordering of the revisions in time);
                if (!string.IsNullOrEmpty(svnREVPROPSPATH))
                {
                    svnClient.GetInfo(trunkexists
                                          ? SvnTarget.FromUri(uri)
                                          : SvnTarget.FromString(dir),
                                      out infoEventArgs);
                    
                    var props = new SvnRevProps(svnREVPROPSPATH, infoEventArgs.Revision);

                    //This helps to make sure the revisions are imported in chronological order
                    props.SetDate(revisions.Keys[0].Time.AddMinutes(-1));
                    
                    
                }

                Cleanup();

                var checkOutArgs = new SvnCheckOutArgs {Depth = SvnDepth.Infinity};
                return svnClient.CheckOut(uri, dir, checkOutArgs);
            }
        }

        #endregion

        #region Revision Process Files

        private static void GetAndAddRevisions()
        {
            using (SvnClient svnClient = GetSvnClient())
            {
                foreach (VssRevProps key in revisions.Keys)
                {
                    migrateLog.InfoFormat("Processing revision {0} / {1}", revisions.IndexOfKey(key) + 1,
                                          revisions.Keys.Count);
                    try
                    {
                        GetAndAddRevision(key, revisions[key], svnClient);
                    }
                    catch (Exception e)
                    {
                        if (IgnoreExceptions)
                        {
                            migrateLog.ErrorFormat(e.ToString());
                        }
                        else
                        {
                            throw;
                        }
                    }
                   
                }
            }
        }

        private static void GetAndAddRevision(VssRevProps properties, Dictionary<string, VssFileVersion> files,
                                              SvnClient svnClient)
        {
            string dir = Path.Combine(repoDIR, vssPROJ.Substring(2).Replace("/", "\\"));
            string filePath = string.Empty;
            var delkeys = new List<string>();
            foreach (string key in files.Keys)
            {
                try
                {
                    //take care of the rare case of an item both file and label
                    if (!String.IsNullOrEmpty(files[key].Version.Label))
                    {

                        if (files[key].Version.VSSItem.Type != 0)
                        {
                            GetFileVersion(files[key].Version.VSSItem, files[key].Version, svnClient);
                            var commitArgs = new SvnCommitArgs {LogMessage = properties.Comment};
                            svnClient.Commit(dir, commitArgs);
                            
                            filePath =
                                Path.Combine(
                                    Path.GetDirectoryName(
                                        string.Format("{0}{1}", repoDIR, files[key].Spec.Substring(1)).Replace("/", "\\")),
                                    files[key].Version.VSSItem.Name);

                            var uri = new Uri(string.Format("{0}{1}", svnURL, svnPROJ));
                            //svnClient.GetInfo(SvnTarget.FromString(filePath), out infoEventArgs);
                            CounterfeitRevProps(svnClient, SvnTarget.FromUri(uri) , files[key].Version);
                        }

                        TagSourceUrl = String.Empty;
                        var tag = new Tag
                                      {
                                          fromUrlString = GenerateSourceUrl(files[key].Version.VSSItem),
                                          tagString = String.Format("{0}/{1}/{2}_{3}",
                                                                    svnURL,
                                                                    svnTAG,
                                                                    files[key].Version.VSSItem.Name,
                                                                    sanitizeLabel(files[key].Version.Label)),
                                          label = files[key].Version.Label
                                      };
                        ApplyTag(files[key].Version.VSSItem, svnClient, files[key].Version, tag);

                        return;

                    } //rare case end

                    GetFileVersion(files[key].Version.VSSItem, files[key].Version, svnClient);
                    //Only need one file to get the revision information from
                    if (string.IsNullOrEmpty(filePath))
                    {
                        filePath =
                            Path.Combine(
                                Path.GetDirectoryName(
                                    string.Format("{0}{1}", repoDIR, files[key].Spec.Substring(1)).Replace("/", "\\")),
                                files[key].Version.VSSItem.Name);
                    }

                    if (files[key].Deleted)
                    {
                        delkeys.Add(filePath);
                    }
                }
                catch (COMException ex)
                {
                    switch ((uint) ex.ErrorCode)
                    {
                        case 0x8004D838: //version is corrupted and unavailable
                            migrateLog.WarnFormat("Skipping version due to corruption in file {1}:{0}",
                                                  files[key].VersionNumber, files[key].Spec);
                            continue;

                        default:
                            if (IgnoreExceptions)
                            {
                                migrateLog.ErrorFormat("Error processing file {1}:{0}",
                                                       files[key].VersionNumber, files[key].Spec);
                                migrateLog.ErrorFormat(ex.ToString());
                                continue;
                            }
                            throw;
                    }
                }
            }
            //Only commit if we actually have a file that has been updated as a result of the get
            if (!string.IsNullOrEmpty(filePath))
            {
                var commitArgs = new SvnCommitArgs {LogMessage = properties.Comment};
                migrateLog.DebugFormat("Committing revision ...");
                //SLOW!!!, change to ICollection of FilePaths (only verify updated files!)
                svnClient.Commit(dir, commitArgs);

                //delete files which are marked as deleted in vss
                foreach (string delFilePath in delkeys)
                {
                    svnClient.Delete(delFilePath);
                    migrateLog.Info(String.Format("Deleted: {0}", delFilePath));
                }
                delkeys.Clear();

                /////////////////////////////////

                SvnInfoEventArgs infoEventArgs;
                //Use one of the committed files to determine the revision we just committed
                var uri = new Uri(string.Format("{0}{1}", svnURL, svnPROJ));
                //svnClient.GetInfo(SvnTarget.FromString(filePath), out infoEventArgs);
                svnClient.GetInfo(SvnTarget.FromUri(uri), out infoEventArgs);

                svnClient.SetRevisionProperty(new Uri("http://svn-repo/svn/sandbox"), infoEventArgs.Revision,
                    SvnPropertyNames.SvnAuthor,
                    properties.Author);

                

                string strCfgTime;
                string[] strSplit, strSplit2;

                strSplit = properties.Time.ToString().Split(' ');
                strSplit2 = strSplit[0].Split('/');

                strCfgTime = strSplit2[2] + '-' + strSplit2[1] + '-' + strSplit2[0] + 'T' + strSplit[1] + ".000000Z";

                //strCfgTime = properties.Time.ToString();
                //strCfgTime = strCfgTime.Replace("/", "-");
                //strCfgTime = strCfgTime.Replace(" ", "T");

                migrateLog.DebugFormat("Date/Time String: " + strCfgTime);

                svnClient.SetRevisionProperty(new Uri("http://svn-repo/svn/sandbox"), infoEventArgs.Revision,
                    SvnPropertyNames.SvnDate,
                    strCfgTime);

                



                /////////////////////////////////

                /*if (!string.IsNullOrEmpty(svnREVPROPSPATH))
                {
                    migrateLog.DebugFormat("Counterfeiting revision properties...");
                    SvnInfoEventArgs infoEventArgs;
                    //Use one of the committed files to determine the revision we just committed
                    var uri = new Uri(string.Format("{0}{1}", svnURL, svnPROJ));
                    //svnClient.GetInfo(SvnTarget.FromString(filePath), out infoEventArgs);
                    svnClient.GetInfo(SvnTarget.FromUri(uri), out infoEventArgs);
                    
                    // (This will fail to work in subversion 1.6 once the shard has been packed)
                    // we use sharpsvn's functions now, so ... not our problem any more :)
                    var props = new SvnRevProps(svnREVPROPSPATH, infoEventArgs.Revision);
                    props.SetAuthor(properties.Author);
                    props.SetDate(properties.Time);
                }*/
            }
        }

        private static void GetFileVersion(IVSSItem vssFile, IVSSVersion vssVersion, SvnClient svnClient)
        {
            numFilesHandled++;
            string dir = string.Format("{0}{1}", repoDIR, vssFile.Spec.Substring(1)).Replace("/", "\\");
            dir = Path.GetDirectoryName(dir);
            string filePath = Path.Combine(dir, vssFile.Name);
            VSSItem versionItem = vssVersion.VSSItem;
            try
            {
                migrateLog.DebugFormat("Fetching VSSItem: {0}, Version: {1}", versionItem.Spec, versionItem.VersionNumber);
                
                if (versionItem.Type != (int)VSSItemType.VSSITEM_PROJECT )
                {

                    versionItem.Get(ref filePath,
                                    (int)
                                    (VSSFlags.VSSFLAG_USERRONO | VSSFlags.VSSFLAG_CMPFAIL | VSSFlags.VSSFLAG_GETYES |
                                     VSSFlags.VSSFLAG_REPREPLACE | VSSFlags.VSSFLAG_TIMEMOD));

                    //kill them *.scc files worth for nothing
                    string[] files = Directory.GetFiles(dir, "*.scc", SearchOption.TopDirectoryOnly);
                    foreach (string file in files)
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                        File.Delete(file);
                    }
                   
                }

                else
                {
                    Directory.CreateDirectory(filePath);
                }

                
                //This seems to fail rather often on binary files and directories, so dont let us use it on them

                if (versionItem.Type != (int)VSSItemType.VSSITEM_PROJECT)
                {
                    if (!versionItem.Binary)
                    {
                        migrateLog.DebugFormat("Diffing ...");
                        if (versionItem.get_IsDifferent(filePath))
                        {
                            migrateLog.WarnFormat(
                                "Files should not be different for version {0}; possible corruption in file {1}:{0}",
                                versionItem.VersionNumber, versionItem.Spec);
                        }
                    }
                }
            }
            catch (COMException e)
            {
                if (e.ErrorCode == -2147166575)
                {
                    // VSS file's checkbox "keep only latest version" is checked therefore no file could be fetched 
                    // so that calling versionItem.Get(...) results in an exception
                    migrateLog.WarnFormat(
                        "Version {0} of file {1} not stored in VSS. File has option 'Keep only latest version' (see file's properties) enabled in VSS",
                        versionItem.VersionNumber, versionItem.Spec);
                }
                else
                {
                    throw;
                }
            }

           VssBindingRemover.RemoveBindings(filePath);

            Collection<SvnStatusEventArgs> svnStatus;
            svnClient.GetStatus(filePath, out svnStatus);

            if (svnStatus.Count == 1)
            {
                SvnStatus fileStatus = svnStatus[0].LocalContentStatus;
                if (fileStatus == SvnStatus.Normal)
                {
                    migrateLog.WarnFormat("No modification detected for {0}:{1}", vssVersion.VSSItem.Spec,
                                          vssVersion.VersionNumber);
                    return;
                }
                if (fileStatus == SvnStatus.NotVersioned || fileStatus == SvnStatus.Incomplete ||
                    fileStatus == SvnStatus.Missing)
                {
                    try
                    {
                        svnClient.Add(filePath);
                    }
                    catch (SvnException e)
                    {
                        if (!e.ToString().Contains("already under version"))
                        {
                            throw;
                        }
                    }
                }
            }
            else
            {
                //Should never get here because we're always looking for the results of an individual file; only display a message
                migrateLog.WarnFormat("Invalid svn status detected for {0}:{1}", vssVersion.VSSItem.Spec,
                                      vssVersion.VersionNumber);
                migrateLog.WarnFormat("Status count was: {0}", svnStatus.Count);
                return;
            }
            //simply continue as expected
        }

        #endregion

        #region Configuration

        private static string GetPrivateProfileString(string settingName)
        {
            return ConfigurationManager.AppSettings[settingName];
        }

        private static bool ReadProperties()
        {
            //Test whether we can load VSS Interop
            //(i.e. do they have an acceptable version of ssapi.dll registered?)

            vssDIR = GetPrivateProfileString("VSSDIR");
            vssSRCSAFEINI = Path.Combine(vssDIR, "srcsafe.ini");
            if (!File.Exists(vssSRCSAFEINI))
            {
                generalLog.ErrorFormat(
                    "VSSDIR does not contain VSS repository (with a srcsafe.ini file).  (Looking for: {0})",
                    vssSRCSAFEINI);
                return false;
            }

            vssPROJ = GetPrivateProfileString("VSSPROJ");
            if (1 > vssPROJ.Length)
            {
                generalLog.Error("VSSPROJ is not set.");
                return false;
            }

            if (vssPROJ == "$")
            {
                vssPROJ = "$/";
            }

            vssUSER = GetPrivateProfileString("VSSUSER");
            vssPASSWORD = GetPrivateProfileString("VSSPASSWORD");

            string pattern = GetPrivateProfileString("VssFolderExclusionRegex");
            if (!string.IsNullOrEmpty(pattern))
            {
                vssFolderExclusionRegex = new Regex(pattern, RegexOptions.IgnoreCase);
            }
            pattern = GetPrivateProfileString("VssFolderInclusionRegex");
            if (!string.IsNullOrEmpty(pattern))
            {
                vssFolderInclusionRegex = new Regex(pattern, RegexOptions.IgnoreCase);
            }

            pattern = GetPrivateProfileString("VssFileExclusionRegex");
            if (!string.IsNullOrEmpty(pattern))
            {
                vssFileExclusionRegex = new Regex(pattern, RegexOptions.IgnoreCase);
            }
            pattern = GetPrivateProfileString("VssFileInclusionRegex");
            if (!string.IsNullOrEmpty(pattern))
            {
                vssFileInclusionRegex = new Regex(pattern, RegexOptions.IgnoreCase);
            }

            //Check to make sure we aren't violating the conditions here...
            if (vssFolderExclusionRegex != null && vssFolderInclusionRegex != null)
            {
                generalLog.Error("Folder regex patterns conflict; use either the inclusion or exclusion but not both");
                return false;
            }
            if (vssFileExclusionRegex != null && vssFileInclusionRegex != null)
            {
                generalLog.Error("File regex patterns conflict; use either the inclusion or exclusion but not both");
                return false;
            }

            svnUSER = GetPrivateProfileString("SVNUSER");
            svnPASSWORD = GetPrivateProfileString("SVNPASSWORD");

            svnURL = GetPrivateProfileString("SVNURL");
            if (string.IsNullOrEmpty(svnURL))
            {
                generalLog.Error("SVNURL is not set");
                return false;
            }
            if (!svnURL.EndsWith("/"))
            {
                svnURL += "/";
            }

            svnPROJ = GetPrivateProfileString("SVNPROJ");
            if (string.IsNullOrEmpty(svnPROJ))
            {
                generalLog.Error("SVNPROJ is not set (also must have a project name e.g. myProject");
                return false;
            }
            if (svnPROJ.StartsWith("/"))
            {
                svnPROJ = svnPROJ.Substring(1);
            }

            svnREVPROPSPATH = GetPrivateProfileString("SVNREVPROPSPATH");
            if (!string.IsNullOrEmpty(svnREVPROPSPATH))
            {
                if (!Directory.Exists(svnREVPROPSPATH))
                {
                    generalLog.Error("SVNREVPROPSPATH not found");
                    return false;
                }
            }

            svnTAG = GetPrivateProfileString("SVNTAG");
            if (string.IsNullOrEmpty(svnTAG))
            {
                Console.Write("SVNTAG is not set!\n");
                return false;
            }
            if (svnTAG.StartsWith("/"))
            {
                svnTAG = svnTAG.Substring(1);
            }

            svnBRANCH = GetPrivateProfileString("SVNBRANCH");
            if (string.IsNullOrEmpty(svnBRANCH))
            {
                Console.Write("SVNBRANCH is not set!\n");
                return false;
            }
            if (svnBRANCH.StartsWith("/"))
            {
                svnBRANCH = svnBRANCH.Substring(1);
            }

            MergeRevisionWindow = int.Parse(GetPrivateProfileString("MergeRevisionWindow"));
                if (MergeRevisionWindow < 0)
                {
                    generalLog.WarnFormat("MergeRevisionWindow is less than 0; resetting {0} to 0", MergeRevisionWindow);
                    MergeRevisionWindow = 0;
                }

                if (!bool.TryParse(GetPrivateProfileString("IgnoreExceptions"), out IgnoreExceptions))
                {
                    generalLog.ErrorFormat("IgnoreExceptions is not a boolean; Use True or False only");
                    return false;
                }
            
            if (!bool.TryParse(GetPrivateProfileString("PerformImport"), out PerformImport))
            {
                generalLog.ErrorFormat("PerformImport is not a boolean; Use True or False only");
                return false;
            }

            outputDIR = GetPrivateProfileString("WORKDIR");
            if (string.IsNullOrEmpty(outputDIR))
            {
                generalLog.Error("WORKDIR parameter is empty");
                return false;
            }

            repoDIR = Path.Combine(outputDIR, "_migrate");
            Directory.CreateDirectory(repoDIR);

            return true;
        }

        #endregion
    }

    internal class Tag
    {
        internal string fromUrlString;
        internal string label;
        internal string tagString;
    }
}