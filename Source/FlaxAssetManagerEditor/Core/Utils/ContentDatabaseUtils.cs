using System.IO;
using FlaxEngine;
using FlaxEditor;

namespace FlaxAssetManager.Core.Utils
{
    /// <summary>
    /// Content database helpers shared by the install-time services. Because the database is
    /// built at editor startup, a folder that a later install writes to disk is unknown to the
    /// database until it is refreshed; these helpers make the tree aware of freshly installed
    /// files and map content-relative paths to their <see cref="FlaxEditor.Content.ContentFolder"/>.
    /// </summary>
    internal static class ContentDatabaseUtils
    {
        /// <summary>
        /// Makes the content database aware of the given (absolute) content destination folder and
        /// any newly installed subfolders/files. Because the database is built at editor startup,
        /// a folder written to disk by a later install is unknown until the database is rebuilt;
        /// this rebuilds it when the folder is missing, then refreshes the folder tree.
        /// Returns false when the folder cannot be found.
        /// </summary>
        public static bool RefreshContentFolder(string root)
        {
            var db = Editor.Instance.ContentDatabase;
            if (db == null) return false;
            // Content items in the database are keyed by absolute path, so look the destination
            // folder up by its absolute location (the relative form would never match).
            var item = db.Find(root);
            if (item == null)
            {
                // The destination folder may have been created on disk after the editor started,
                // so the content database (built at startup) doesn't know about it. Rebuild the
                // database to rescan the content tree and pick up the new folders/files.
                db.Rebuild(immediate: true);
                item = db.Find(root);
            }
            if (item == null)
            {
                Debug.LogWarning($"FlaxAssetManager: content root '{root}' not found in content database; cannot refresh.");
                return false;
            }
            db.RefreshFolder(item, checkSubDirs: true);
            return true;
        }

        /// <summary>
        /// Finds a <see cref="FlaxEditor.Content.ContentFolder"/> at the given content-relative path.
        /// </summary>
        public static FlaxEditor.Content.ContentFolder FindContentFolder(string relativePath)
        {
            var db = Editor.Instance.ContentDatabase;
            if (db == null) return null;
            // Strip any trailing separator so the folder lookup is exact.
            while (relativePath.EndsWith("/"))
                relativePath = relativePath.Substring(0, relativePath.Length - 1);
            // Content items are keyed by absolute path; resolve the project-relative path
            // against the project folder before the lookup.
            var item = db.Find(Path.Combine(Globals.ProjectFolder, relativePath));
            return item as FlaxEditor.Content.ContentFolder;
        }
    }
}
