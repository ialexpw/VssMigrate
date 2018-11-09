# VssMigrate
A tool, written in C#, for importing a Visual SourceSafe database into Subversion (SVN) while maintaining the complete file history.

## Alterations made
The code as-is on CodePlex (below) will work at migrating a Visual Source Safe (2005) Repository to Subversion. The changes I have made were needed for our conversion and it may help other people.

When doing the migration, all of the check-in dates and authors will be set to the current date/time and the Subversion user that you are using. There is code within the original that should allow you to change this, but it no longer works with new versions of Subversion.

The alterations made in this code allow the original authors and dates (from SourceSafe) to be used keeping your complete and correct history. The changes are not pretty - but they work.

## Last version from CodePlex

* Changeset 16890 Resolves the individual VSS-checkins to atomic SVN commits via the approximate timestamp. We hope to promote this to a release version soon, but feel free to try, as people have been having good luck with it.
* Written in C# using .NET 3.5
* Utilizes SharpSvn and VSS Interop.
* Preserves the username and revision datetime via direct editing of the revprops folder in the SVN repository. This requires direct file system access to the SVN repository and is not a recommended practice, but it works quite well and does not require editing the system clock or matching any usernames/passwords
* Removes all VSS bindings and related files during the import
* Use VSS 2005 for best results
* Support for tags

The original project was started by [Doug](http://www.poweradmin.com/sourcecode/vssmigrate.aspx) and ported to C# by [Tim Erickson](http://www.codeplex.com/site/users/view/timerickson).

Original code from https://archive.codeplex.com/?p=vssmigrate
