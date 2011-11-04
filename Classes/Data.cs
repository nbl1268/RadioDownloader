/* 
 * This file is part of Radio Downloader.
 * Copyright © 2007-2011 Matt Robinson
 * 
 * This program is free software: you can redistribute it and/or modify it under the terms of the GNU General
 * Public License as published by the Free Software Foundation, either version 3 of the License, or (at your
 * option) any later version.
 * 
 * This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the
 * implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public
 * License for more details.
 * 
 * You should have received a copy of the GNU General Public License along with this program.  If not, see
 * <http://www.gnu.org/licenses/>.
 */

namespace RadioDld
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Data.SQLite;
    using System.Drawing;
    using System.Globalization;
    using System.IO;
    using System.Threading;
    using System.Windows.Forms;
    using System.Xml.Serialization;
    using Microsoft.VisualBasic;

    internal class Data
    {
        [ThreadStatic]
        private static SQLiteConnection dbConn;
        private static Data dataInstance;

        private static object dataInstanceLock = new object();

        private static object dbUpdateLock = new object();

        private DataSearch search;
        private Thread episodeListThread;

        private object episodeListThreadLock = new object();
        private DldProgData curDldProgData;
        private Thread downloadThread;

        private IRadioProvider downloadPluginInst;
        private IRadioProvider findNewPluginInst;

        private object findDownloadLock = new object();
        private int lastProgressVal = -1;

        private Data()
            : base()
        {
            // Vacuum the database every few months - vacuums are spaced like this as they take ages to run
            bool runVacuum = false;
            object lastVacuum = this.GetDBSetting("lastvacuum");

            if (lastVacuum == null)
            {
                runVacuum = true;
            }
            else
            {
                runVacuum = DateTime.ParseExact((string)lastVacuum, "O", CultureInfo.InvariantCulture).AddMonths(3) < DateTime.Now;
            }

            if (runVacuum)
            {
                using (Status status = new Status())
                {
                    status.ShowDialog(delegate
                    {
                        this.VacuumDatabase(status);
                    });
                }
            }

            const int CurrentVer = 4;
            int dbVersion = CurrentVer;

            // Fetch the version of the database
            if (this.GetDBSetting("databaseversion") != null)
            {
                dbVersion = Convert.ToInt32(this.GetDBSetting("databaseversion"), CultureInfo.InvariantCulture);
            }

            switch (dbVersion)
            {
                case 4:
                    // Nothing to do, this is the current version.
                    break;
            }

            // Set the current database version.
            this.SetDBSetting("databaseversion", CurrentVer);

            this.search = DataSearch.GetInstance();

            // Start regularly checking for new subscriptions in the background
            ThreadPool.QueueUserWorkItem(delegate
            {
                // Wait for 2 minutes while the application starts
                Thread.Sleep(120000);
                this.CheckSubscriptions();
            });
        }

        public delegate void ProviderAddedEventHandler(Guid providerId);

        public delegate void FindNewViewChangeEventHandler(object viewData);

        public delegate void FoundNewEventHandler(int progid);

        public delegate void EpisodeAddedEventHandler(int epid);

        public delegate void DownloadProgressEventHandler(int epid, int percent, string statusText, ProgressIcon icon);

        public delegate void DownloadProgressTotalEventHandler(bool downloading, int percent);

        public event ProviderAddedEventHandler ProviderAdded;

        public event FindNewViewChangeEventHandler FindNewViewChange;

        public event FoundNewEventHandler FoundNew;

        public event EpisodeAddedEventHandler EpisodeAdded;

        public event DownloadProgressEventHandler DownloadProgress;

        public event DownloadProgressTotalEventHandler DownloadProgressTotal;

        public static object DbUpdateLock
        {
            get
            {
                return dbUpdateLock;
            }
        }

        public static Data GetInstance()
        {
            // Need to use a lock instead of declaring the instance variable as New, as otherwise
            // on first run the constructor gets called before the template database is in place
            lock (dataInstanceLock)
            {
                if (dataInstance == null)
                {
                    dataInstance = new Data();
                }

                return dataInstance;
            }
        }

        public void StartDownload()
        {
            ThreadPool.QueueUserWorkItem(delegate { this.StartDownloadAsync(); });
        }

        public Bitmap FetchProgrammeImage(int progid)
        {
            using (SQLiteCommand command = new SQLiteCommand("select image from programmes where progid=@progid and image not null", FetchDbConn()))
            {
                command.Parameters.Add(new SQLiteParameter("@progid", progid));

                using (SQLiteMonDataReader reader = new SQLiteMonDataReader(command.ExecuteReader()))
                {
                    if (reader.Read())
                    {
                        return this.RetrieveImage(reader.GetInt32(reader.GetOrdinal("image")));
                    }
                    else
                    {
                        // Find the id of the latest episode's image
                        using (SQLiteCommand latestCmd = new SQLiteCommand("select image from episodes where progid=@progid and image not null order by date desc limit 1", FetchDbConn()))
                        {
                            latestCmd.Parameters.Add(new SQLiteParameter("@progid", progid));

                            using (SQLiteMonDataReader latestRdr = new SQLiteMonDataReader(latestCmd.ExecuteReader()))
                            {
                                if (latestRdr.Read())
                                {
                                    return this.RetrieveImage(latestRdr.GetInt32(latestRdr.GetOrdinal("image")));
                                }
                            }
                        }
                    }
                }
            }

            return null;
        }

        public Bitmap FetchEpisodeImage(int epid)
        {
            using (SQLiteCommand command = new SQLiteCommand("select image, progid from episodes where epid=@epid", FetchDbConn()))
            {
                command.Parameters.Add(new SQLiteParameter("@epid", epid));

                using (SQLiteMonDataReader reader = new SQLiteMonDataReader(command.ExecuteReader()))
                {
                    if (reader.Read())
                    {
                        int imageOrdinal = reader.GetOrdinal("image");

                        if (!reader.IsDBNull(imageOrdinal))
                        {
                            return this.RetrieveImage(reader.GetInt32(imageOrdinal));
                        }

                        int progidOrdinal = reader.GetOrdinal("progid");

                        if (!reader.IsDBNull(progidOrdinal))
                        {
                            using (SQLiteCommand progCmd = new SQLiteCommand("select image from programmes where progid=@progid and image not null", FetchDbConn()))
                            {
                                progCmd.Parameters.Add(new SQLiteParameter("@progid", reader.GetInt32(progidOrdinal)));

                                using (SQLiteMonDataReader progReader = new SQLiteMonDataReader(progCmd.ExecuteReader()))
                                {
                                    if (progReader.Read())
                                    {
                                        return this.RetrieveImage(progReader.GetInt32(progReader.GetOrdinal("image")));
                                    }
                                }
                            }
                        }
                    }
                }
            }

            return null;
        }

        public void DownloadReportError(int epid)
        {
            ErrorType errorType = default(ErrorType);
            string errorText = null;
            string extraDetailsString = null;
            Dictionary<string, string> errorExtraDetails = new Dictionary<string, string>();

            XmlSerializer detailsSerializer = new XmlSerializer(typeof(List<DldErrorDataItem>));

            using (SQLiteCommand command = new SQLiteCommand("select errortype, errordetails, ep.name as epname, ep.description as epdesc, date, duration, ep.extid as epextid, pr.name as progname, pr.description as progdesc, pr.extid as progextid, pluginid from downloads as dld, episodes as ep, programmes as pr where dld.epid=@epid and ep.epid=@epid and ep.progid=pr.progid", FetchDbConn()))
            {
                command.Parameters.Add(new SQLiteParameter("@epid", epid));

                using (SQLiteMonDataReader reader = new SQLiteMonDataReader(command.ExecuteReader()))
                {
                    if (!reader.Read())
                    {
                        throw new ArgumentException("Episode " + epid.ToString(CultureInfo.InvariantCulture) + " does not exit, or is not in the download list!", "epid");
                    }

                    errorType = (ErrorType)reader.GetInt32(reader.GetOrdinal("errortype"));
                    extraDetailsString = reader.GetString(reader.GetOrdinal("errordetails"));

                    errorExtraDetails.Add("episode:name", reader.GetString(reader.GetOrdinal("epname")));
                    errorExtraDetails.Add("episode:description", reader.GetString(reader.GetOrdinal("epdesc")));
                    errorExtraDetails.Add("episode:date", reader.GetDateTime(reader.GetOrdinal("date")).ToString("yyyy-MM-dd hh:mm", CultureInfo.InvariantCulture));
                    errorExtraDetails.Add("episode:duration", Convert.ToString(reader.GetInt32(reader.GetOrdinal("duration")), CultureInfo.InvariantCulture));
                    errorExtraDetails.Add("episode:extid", reader.GetString(reader.GetOrdinal("epextid")));

                    errorExtraDetails.Add("programme:name", reader.GetString(reader.GetOrdinal("progname")));
                    errorExtraDetails.Add("programme:description", reader.GetString(reader.GetOrdinal("progdesc")));
                    errorExtraDetails.Add("programme:extid", reader.GetString(reader.GetOrdinal("progextid")));

                    Guid pluginId = new Guid(reader.GetString(reader.GetOrdinal("pluginid")));
                    IRadioProvider providerInst = Plugins.GetPluginInstance(pluginId);

                    errorExtraDetails.Add("provider:id", pluginId.ToString());
                    errorExtraDetails.Add("provider:name", providerInst.ProviderName);
                    errorExtraDetails.Add("provider:description", providerInst.ProviderDescription);
                }
            }

            if (extraDetailsString != null)
            {
                try
                {
                    List<DldErrorDataItem> extraDetails = null;
                    extraDetails = (List<DldErrorDataItem>)detailsSerializer.Deserialize(new StringReader(extraDetailsString));

                    foreach (DldErrorDataItem detailItem in extraDetails)
                    {
                        switch (detailItem.Name)
                        {
                            case "error":
                                errorText = detailItem.Data;
                                break;
                            case "details":
                                extraDetailsString = detailItem.Data;
                                break;
                            default:
                                errorExtraDetails.Add(detailItem.Name, detailItem.Data);
                                break;
                        }
                    }
                }
                catch (InvalidOperationException)
                {
                    // Do nothing, and fall back to reporting all the details as one string
                }
                catch (InvalidCastException)
                {
                    // Do nothing, and fall back to reporting all the details as one string
                }
            }

            if (string.IsNullOrEmpty(errorText))
            {
                errorText = errorType.ToString();
            }

            ErrorReporting report = new ErrorReporting("Download Error: " + errorText, extraDetailsString, errorExtraDetails);
            report.SendReport(Properties.Settings.Default.ErrorReportURL);
        }

        public Panel GetFindNewPanel(Guid pluginID, object view)
        {
            if (Plugins.PluginExists(pluginID))
            {
                this.findNewPluginInst = Plugins.GetPluginInstance(pluginID);
                this.findNewPluginInst.FindNewException += this.FindNewPluginInst_FindNewException;
                this.findNewPluginInst.FindNewViewChange += this.FindNewPluginInst_FindNewViewChange;
                this.findNewPluginInst.FoundNew += this.FindNewPluginInst_FoundNew;
                return this.findNewPluginInst.GetFindNewPanel(view);
            }
            else
            {
                return new Panel();
            }
        }

        public void InitProviderList()
        {
            Guid[] pluginIdList = null;
            pluginIdList = Plugins.GetPluginIdList();

            foreach (Guid pluginId in pluginIdList)
            {
                if (this.ProviderAdded != null)
                {
                    this.ProviderAdded(pluginId);
                }
            }
        }

        public void InitEpisodeList(int progid)
        {
            lock (this.episodeListThreadLock)
            {
                this.episodeListThread = new Thread(() => this.InitEpisodeListThread(progid));
                this.episodeListThread.IsBackground = true;
                this.episodeListThread.Start();
            }
        }

        public void CancelEpisodeListing()
        {
            lock (this.episodeListThreadLock)
            {
                this.episodeListThread = null;
            }
        }

        public ProviderData FetchProviderData(Guid providerId)
        {
            IRadioProvider providerInstance = Plugins.GetPluginInstance(providerId);

            ProviderData info = new ProviderData();
            info.Name = providerInstance.ProviderName;
            info.Description = providerInstance.ProviderDescription;
            info.Icon = providerInstance.ProviderIcon;
            info.ShowOptionsHandler = providerInstance.GetShowOptionsHandler();

            return info;
        }

        public void DownloadCancel(int epid, bool auto)
        {
            if (this.curDldProgData != null)
            {
                if (this.curDldProgData.EpisodeInfo.Epid == epid)
                {
                    // This episode is currently being downloaded
                    if (this.downloadThread != null)
                    {
                        if (!auto)
                        {
                            // This is called by the download thread if it is an automatic removal
                            this.downloadThread.Abort();
                        }

                        this.downloadThread = null;
                    }
                }

                this.StartDownload();
            }
        }

        public int? StoreImage(Bitmap image)
        {
            if (image == null)
            {
                return null;
            }

            // Convert the image into a byte array
            byte[] imageAsBytes = null;

            using (MemoryStream memstream = new MemoryStream())
            {
                image.Save(memstream, System.Drawing.Imaging.ImageFormat.Png);
                imageAsBytes = memstream.ToArray();
            }

            lock (Data.dbUpdateLock)
            {
                using (SQLiteCommand command = new SQLiteCommand("select imgid from images where image=@image", FetchDbConn()))
                {
                    command.Parameters.Add(new SQLiteParameter("@image", imageAsBytes));

                    using (SQLiteMonDataReader reader = new SQLiteMonDataReader(command.ExecuteReader()))
                    {
                        if (reader.Read())
                        {
                            return reader.GetInt32(reader.GetOrdinal("imgid"));
                        }
                    }
                }

                using (SQLiteCommand command = new SQLiteCommand("insert into images (image) values (@image)", FetchDbConn()))
                {
                    command.Parameters.Add(new SQLiteParameter("@image", imageAsBytes));
                    command.ExecuteNonQuery();
                }

                using (SQLiteCommand command = new SQLiteCommand("select last_insert_rowid()", FetchDbConn()))
                {
                    return (int)(long)command.ExecuteScalar();
                }
            }
        }

        internal static SQLiteConnection FetchDbConn()
        {
            if (dbConn == null)
            {
                dbConn = new SQLiteConnection("Data Source=" + Path.Combine(FileUtils.GetAppDataFolder(), "store.db") + ";Version=3;New=False");
                dbConn.Open();
            }

            return dbConn;
        }

        private void StartDownloadAsync()
        {
            lock (this.findDownloadLock)
            {
                if (this.downloadThread == null)
                {
                    using (SQLiteCommand command = new SQLiteCommand("select pr.progid, pluginid, pr.image as progimg, ep.duration, ep.image as epimg, pr.extid as progextid, ep.extid as epextid, dl.status, ep.epid from downloads as dl, episodes as ep, programmes as pr where dl.epid=ep.epid and ep.progid=pr.progid and (dl.status=@statuswait or (dl.status=@statuserr and dl.errortime < datetime('now', '-' || power(2, dl.errorcount) || ' hours'))) order by ep.date", FetchDbConn()))
                    {
                        command.Parameters.Add(new SQLiteParameter("@statuswait", Model.Download.DownloadStatus.Waiting));
                        command.Parameters.Add(new SQLiteParameter("@statuserr", Model.Download.DownloadStatus.Errored));

                        using (SQLiteMonDataReader reader = new SQLiteMonDataReader(command.ExecuteReader()))
                        {
                            while (this.downloadThread == null)
                            {
                                if (!reader.Read())
                                {
                                    return;
                                }

                                Guid pluginId = new Guid(reader.GetString(reader.GetOrdinal("pluginid")));

                                if (Plugins.PluginExists(pluginId))
                                {
                                    Model.Programme progInfo = new Model.Programme(reader.GetInt32(reader.GetOrdinal("progid")));
                                    Model.Episode epInfo = new Model.Episode(reader.GetInt32(reader.GetOrdinal("epid")));
                                    ProgrammeInfo provProgInfo = new ProgrammeInfo();

                                    provProgInfo.Name = progInfo.Name;
                                    provProgInfo.Description = progInfo.Description;

                                    if (reader.IsDBNull(reader.GetOrdinal("progimg")))
                                    {
                                        provProgInfo.Image = null;
                                    }
                                    else
                                    {
                                        provProgInfo.Image = this.RetrieveImage(reader.GetInt32(reader.GetOrdinal("progimg")));
                                    }

                                    EpisodeInfo provEpInfo = new EpisodeInfo();
                                    provEpInfo.Name = progInfo.Name;
                                    provEpInfo.Description = progInfo.Description;
                                    provEpInfo.Date = epInfo.EpisodeDate;

                                    if (reader.IsDBNull(reader.GetOrdinal("duration")))
                                    {
                                        provEpInfo.DurationSecs = null;
                                    }
                                    else
                                    {
                                        provEpInfo.DurationSecs = reader.GetInt32(reader.GetOrdinal("duration"));
                                    }

                                    if (reader.IsDBNull(reader.GetOrdinal("epimg")))
                                    {
                                        provEpInfo.Image = null;
                                    }
                                    else
                                    {
                                        provEpInfo.Image = this.RetrieveImage(reader.GetInt32(reader.GetOrdinal("epimg")));
                                    }

                                    provEpInfo.ExtInfo = new Dictionary<string, string>();

                                    using (SQLiteCommand extCommand = new SQLiteCommand("select name, value from episodeext where epid=@epid", FetchDbConn()))
                                    {
                                        extCommand.Parameters.Add(new SQLiteParameter("@epid", reader.GetInt32(reader.GetOrdinal("epid"))));

                                        using (SQLiteMonDataReader extReader = new SQLiteMonDataReader(extCommand.ExecuteReader()))
                                        {
                                            while (extReader.Read())
                                            {
                                                provEpInfo.ExtInfo.Add(extReader.GetString(extReader.GetOrdinal("name")), extReader.GetString(extReader.GetOrdinal("value")));
                                            }
                                        }
                                    }

                                    this.curDldProgData = new DldProgData();
                                    this.curDldProgData.PluginId = pluginId;
                                    this.curDldProgData.ProgExtId = reader.GetString(reader.GetOrdinal("progextid"));
                                    this.curDldProgData.EpisodeExtId = reader.GetString(reader.GetOrdinal("epextid"));
                                    this.curDldProgData.ProgInfo = progInfo;
                                    this.curDldProgData.ProviderProgInfo = provProgInfo;
                                    this.curDldProgData.EpisodeInfo = epInfo;
                                    this.curDldProgData.ProviderEpisodeInfo = provEpInfo;

                                    if ((Model.Download.DownloadStatus)reader.GetInt32(reader.GetOrdinal("status")) == Model.Download.DownloadStatus.Errored)
                                    {
                                        Model.Download.ResetAsync(epInfo.Epid, true);
                                    }

                                    this.downloadThread = new Thread(this.DownloadProgThread);
                                    this.downloadThread.IsBackground = true;
                                    this.downloadThread.Start();

                                    return;
                                }
                            }
                        }
                    }
                }
            }
        }

        private void DownloadProgThread()
        {
            this.lastProgressVal = -1;

            this.downloadPluginInst = Plugins.GetPluginInstance(this.curDldProgData.PluginId);
            this.downloadPluginInst.Finished += this.DownloadPluginInst_Finished;
            this.downloadPluginInst.Progress += this.DownloadPluginInst_Progress;

            try
            {
                // Make sure that the temp folder still exists
                Directory.CreateDirectory(Path.Combine(System.IO.Path.GetTempPath(), "RadioDownloader"));

                try
                {
                    this.curDldProgData.FinalName = Model.Download.FindFreeSaveFileName(Properties.Settings.Default.FileNameFormat, this.curDldProgData.ProgInfo, this.curDldProgData.EpisodeInfo, FileUtils.GetSaveFolder());
                }
                catch (DirectoryNotFoundException)
                {
                    this.DownloadError(ErrorType.LocalProblem, "Your chosen location for saving downloaded programmes no longer exists.  Select a new one under Options -> Main Options.", null);
                    return;
                }
                catch (IOException ioExp)
                {
                    this.DownloadError(ErrorType.LocalProblem, "Encountered an error generating the download file name.  " + ioExp.Message + "  You may need to select a new location for saving downloaded programmes under Options -> Main Options.", null);
                    return;
                }
                catch (UnauthorizedAccessException unAuthExp)
                {
                    this.DownloadError(ErrorType.LocalProblem, "Encountered a permissions problem generating the download file name.  " + unAuthExp.Message + "  You may need to select a new location for saving downloaded programmes under Options -> Main Options.", null);
                    return;
                }

                this.downloadPluginInst.DownloadProgramme(this.curDldProgData.ProgExtId, this.curDldProgData.EpisodeExtId, this.curDldProgData.ProviderProgInfo, this.curDldProgData.ProviderEpisodeInfo, this.curDldProgData.FinalName);
            }
            catch (ThreadAbortException)
            {
                // The download has been aborted, so ignore the exception
            }
            catch (DownloadException downloadExp)
            {
                this.DownloadError(downloadExp.TypeOfError, downloadExp.Message, downloadExp.ErrorExtraDetails);
            }
            catch (Exception unknownExp)
            {
                List<DldErrorDataItem> extraDetails = new List<DldErrorDataItem>();
                extraDetails.Add(new DldErrorDataItem("error", unknownExp.GetType().ToString() + ": " + unknownExp.Message));
                extraDetails.Add(new DldErrorDataItem("exceptiontostring", unknownExp.ToString()));

                if (unknownExp.Data != null)
                {
                    foreach (DictionaryEntry dataEntry in unknownExp.Data)
                    {
                        if (object.ReferenceEquals(dataEntry.Key.GetType(), typeof(string)) && object.ReferenceEquals(dataEntry.Value.GetType(), typeof(string)))
                        {
                            extraDetails.Add(new DldErrorDataItem("expdata:Data:" + (string)dataEntry.Key, (string)dataEntry.Value));
                        }
                    }
                }

                this.DownloadError(ErrorType.UnknownError, unknownExp.GetType().ToString() + Environment.NewLine + unknownExp.StackTrace, extraDetails);
            }
        }

        private void CheckSubscriptions()
        {
            // Fetch the current subscriptions into a list, so that the reader doesn't remain open while
            // checking all of the subscriptions, as this blocks writes to the database from other threads
            List<Model.Subscription> subscriptions = Model.Subscription.FetchAll();

            // Work through the list of subscriptions and check for new episodes
            using (SQLiteCommand progInfCmd = new SQLiteCommand("select pluginid, extid from programmes where progid=@progid", FetchDbConn()))
            {
                using (SQLiteCommand checkCmd = new SQLiteCommand("select epid from downloads where epid=@epid", FetchDbConn()))
                {
                    using (SQLiteCommand findCmd = new SQLiteCommand("select epid, autodownload from episodes where progid=@progid and extid=@extid", FetchDbConn()))
                    {
                        SQLiteParameter epidParam = new SQLiteParameter("@epid");
                        SQLiteParameter progidParam = new SQLiteParameter("@progid");
                        SQLiteParameter extidParam = new SQLiteParameter("@extid");

                        progInfCmd.Parameters.Add(progidParam);
                        findCmd.Parameters.Add(progidParam);
                        findCmd.Parameters.Add(extidParam);
                        checkCmd.Parameters.Add(epidParam);

                        foreach (Model.Subscription subscription in subscriptions)
                        {
                            Guid providerId = default(Guid);
                            string progExtId = null;

                            progidParam.Value = subscription.Progid;

                            using (SQLiteMonDataReader progInfReader = new SQLiteMonDataReader(progInfCmd.ExecuteReader()))
                            {
                                if (!progInfReader.Read())
                                {
                                    continue;
                                }

                                providerId = new Guid(progInfReader.GetString(progInfReader.GetOrdinal("pluginid")));
                                progExtId = progInfReader.GetString(progInfReader.GetOrdinal("extid"));
                            }

                            List<string> episodeExtIds = null;

                            try
                            {
                                episodeExtIds = this.GetAvailableEpisodes(providerId, progExtId);
                            }
                            catch (Exception)
                            {
                                // Catch any unhandled provider exceptions
                                continue;
                            }

                            if (episodeExtIds != null)
                            {
                                foreach (string episodeExtId in episodeExtIds)
                                {
                                    extidParam.Value = episodeExtId;

                                    bool needEpInfo = true;
                                    int epid = 0;

                                    using (SQLiteMonDataReader findReader = new SQLiteMonDataReader(findCmd.ExecuteReader()))
                                    {
                                        if (findReader.Read())
                                        {
                                            needEpInfo = false;
                                            epid = findReader.GetInt32(findReader.GetOrdinal("epid"));

                                            if (!subscription.SingleEpisode)
                                            {
                                                if (findReader.GetInt32(findReader.GetOrdinal("autodownload")) != 1)
                                                {
                                                    // Don't download the episode automatically, skip to the next one
                                                    continue;
                                                }
                                            }
                                        }
                                    }

                                    if (needEpInfo)
                                    {
                                        try
                                        {
                                            epid = this.StoreEpisodeInfo(providerId, subscription.Progid, progExtId, episodeExtId);
                                        }
                                        catch
                                        {
                                            // Catch any unhandled provider exceptions
                                            continue;
                                        }

                                        if (epid < 0)
                                        {
                                            continue;
                                        }
                                    }

                                    epidParam.Value = epid;

                                    using (SQLiteMonDataReader checkRdr = new SQLiteMonDataReader(checkCmd.ExecuteReader()))
                                    {
                                        if (!checkRdr.Read())
                                        {
                                            Model.Download.Add(epid);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // Wait for 10 minutes to give a pause between each check for new episodes
            Thread.Sleep(600000);

            // Queue the next subscription check.  This is used instead of a loop
            // as it frees up a slot in the thread pool other actions are waiting.
            ThreadPool.QueueUserWorkItem(delegate { this.CheckSubscriptions(); });
        }

        private void DownloadError(ErrorType errorType, string errorDetails, List<DldErrorDataItem> furtherDetails)
        {
            Model.Download.SetErrorred(this.curDldProgData.EpisodeInfo.Epid, errorType, errorDetails, furtherDetails);

            if (this.DownloadProgressTotal != null)
            {
                this.DownloadProgressTotal(false, 0);
            }

            this.downloadThread = null;
            this.curDldProgData = null;

            this.StartDownloadAsync();
        }

        private void DownloadPluginInst_Finished(string fileExtension)
        {
            this.curDldProgData.FinalName += "." + fileExtension;

            lock (Data.dbUpdateLock)
            {
                Model.Download.SetComplete(this.curDldProgData.EpisodeInfo.Epid, this.curDldProgData.FinalName);
                Model.Programme.SetLatestDownload(this.curDldProgData.ProgInfo.Progid, this.curDldProgData.EpisodeInfo.EpisodeDate);
            }

            if (this.DownloadProgressTotal != null)
            {
                this.DownloadProgressTotal(false, 100);
            }

            // Remove single episode subscriptions
            if (Model.Subscription.IsSubscribed(this.curDldProgData.ProgInfo.Progid))
            {
                if (this.curDldProgData.ProgInfo.SingleEpisode)
                {
                    Model.Subscription.Remove(this.curDldProgData.ProgInfo.Progid);
                }
            }

            if (!string.IsNullOrEmpty(Properties.Settings.Default.RunAfterCommand))
            {
                try
                {
                    // Environ("comspec") will give the path to cmd.exe or command.com
                    Interaction.Shell("\"" + Interaction.Environ("comspec") + "\" /c " + Properties.Settings.Default.RunAfterCommand.Replace("%file%", this.curDldProgData.FinalName), AppWinStyle.NormalNoFocus);
                }
                catch
                {
                    // Just ignore the error, as it just means that something has gone wrong with the run after command.
                }
            }

            this.downloadThread = null;
            this.curDldProgData = null;

            this.StartDownloadAsync();
        }

        private void DownloadPluginInst_Progress(int percent, string statusText, ProgressIcon icon)
        {
            // Don't raise the progress event if the value is the same as last time, or is outside the range
            if (percent == this.lastProgressVal || percent < 0 || percent > 100)
            {
                return;
            }

            this.lastProgressVal = percent;

            if (this.search.DownloadIsVisible(this.curDldProgData.EpisodeInfo.Epid))
            {
                if (this.DownloadProgress != null)
                {
                    this.DownloadProgress(this.curDldProgData.EpisodeInfo.Epid, percent, statusText, icon);
                }
            }

            if (this.DownloadProgressTotal != null)
            {
                this.DownloadProgressTotal(true, percent);
            }
        }

        private void SetDBSetting(string propertyName, object value)
        {
            lock (Data.dbUpdateLock)
            {
                using (SQLiteCommand command = new SQLiteCommand("insert or replace into settings (property, value) values (@property, @value)", FetchDbConn()))
                {
                    command.Parameters.Add(new SQLiteParameter("@property", propertyName));
                    command.Parameters.Add(new SQLiteParameter("@value", value));
                    command.ExecuteNonQuery();
                }
            }
        }

        private object GetDBSetting(string propertyName)
        {
            using (SQLiteCommand command = new SQLiteCommand("select value from settings where property=@property", FetchDbConn()))
            {
                command.Parameters.Add(new SQLiteParameter("@property", propertyName));

                using (SQLiteMonDataReader reader = new SQLiteMonDataReader(command.ExecuteReader()))
                {
                    if (!reader.Read())
                    {
                        return null;
                    }

                    return reader.GetValue(reader.GetOrdinal("value"));
                }
            }
        }

        private void VacuumDatabase(Status status)
        {
            status.StatusText = "Compacting database.  This may take several minutes...";

            // Make SQLite recreate the database to reduce the size on disk and remove fragmentation
            lock (Data.dbUpdateLock)
            {
                using (SQLiteCommand command = new SQLiteCommand("vacuum", FetchDbConn()))
                {
                    command.ExecuteNonQuery();
                }

                this.SetDBSetting("lastvacuum", DateTime.Now.ToString("O", CultureInfo.InvariantCulture));
            }
        }

        private void FindNewPluginInst_FindNewException(Exception exception, bool unhandled)
        {
            if (unhandled)
            {
                ErrorReporting report = new ErrorReporting(exception);

                using (ReportError showError = new ReportError())
                {
                    showError.ShowReport(report);
                }
            }
            else
            {
                ErrorReporting reportException = new ErrorReporting("Find New Error", exception);
                reportException.SendReport(Properties.Settings.Default.ErrorReportURL);
            }
        }

        private void FindNewPluginInst_FindNewViewChange(object view)
        {
            if (this.FindNewViewChange != null)
            {
                this.FindNewViewChange(view);
            }
        }

        private void FindNewPluginInst_FoundNew(string progExtId)
        {
            Guid pluginId = this.findNewPluginInst.ProviderId;
            int? progid = Model.Programme.FetchInfo(pluginId, progExtId);

            if (progid == null)
            {
                Interaction.MsgBox("There was a problem retrieving information about this programme.  You might like to try again later.", MsgBoxStyle.Exclamation);
                return;
            }

            if (this.FoundNew != null)
            {
                this.FoundNew(progid.Value);
            }
        }

        private Bitmap RetrieveImage(int imgid)
        {
            using (SQLiteCommand command = new SQLiteCommand("select image from images where imgid=@imgid", FetchDbConn()))
            {
                command.Parameters.Add(new SQLiteParameter("@imgid", imgid));

                using (SQLiteMonDataReader reader = new SQLiteMonDataReader(command.ExecuteReader()))
                {
                    if (!reader.Read())
                    {
                        return null;
                    }

                    // Get the size of the image data by passing nothing to getbytes
                    int dataLength = (int)reader.GetBytes(reader.GetOrdinal("image"), 0, null, 0, 0);
                    byte[] content = new byte[dataLength];

                    reader.GetBytes(reader.GetOrdinal("image"), 0, content, 0, dataLength);

                    using (MemoryStream contentStream = new MemoryStream(content))
                    {
                        using (Bitmap streamBitmap = new Bitmap(contentStream))
                        {
                            return new Bitmap(streamBitmap);
                        }
                    }
                }
            }
        }

        private List<string> GetAvailableEpisodes(Guid providerId, string progExtId)
        {
            if (!Plugins.PluginExists(providerId))
            {
                return null;
            }

            string[] extIds = null;
            IRadioProvider providerInst = Plugins.GetPluginInstance(providerId);

            extIds = providerInst.GetAvailableEpisodeIds(progExtId);

            if (extIds == null)
            {
                return null;
            }

            // Remove any duplicates from the list of episodes
            List<string> extIdsUnique = new List<string>();

            foreach (string removeDups in extIds)
            {
                if (!extIdsUnique.Contains(removeDups))
                {
                    extIdsUnique.Add(removeDups);
                }
            }

            return extIdsUnique;
        }

        private int StoreEpisodeInfo(Guid pluginId, int progid, string progExtId, string episodeExtId)
        {
            IRadioProvider providerInst = Plugins.GetPluginInstance(pluginId);
            GetEpisodeInfoReturn episodeInfoReturn = default(GetEpisodeInfoReturn);

            episodeInfoReturn = providerInst.GetEpisodeInfo(progExtId, episodeExtId);

            if (!episodeInfoReturn.Success)
            {
                return -1;
            }

            if (string.IsNullOrEmpty(episodeInfoReturn.EpisodeInfo.Name))
            {
                throw new InvalidDataException("Episode name cannot be null or an empty string");
            }

            if (episodeInfoReturn.EpisodeInfo.Date == null)
            {
                // The date of the episode isn't known, so use the current date
                episodeInfoReturn.EpisodeInfo.Date = DateTime.Now;
            }

            lock (Data.dbUpdateLock)
            {
                using (SQLiteMonTransaction transMon = new SQLiteMonTransaction(FetchDbConn().BeginTransaction()))
                {
                    int epid = 0;

                    using (SQLiteCommand addEpisodeCmd = new SQLiteCommand("insert into episodes (progid, extid, name, description, duration, date, image) values (@progid, @extid, @name, @description, @duration, @date, @image)", FetchDbConn(), transMon.Trans))
                    {
                        addEpisodeCmd.Parameters.Add(new SQLiteParameter("@progid", progid));
                        addEpisodeCmd.Parameters.Add(new SQLiteParameter("@extid", episodeExtId));
                        addEpisodeCmd.Parameters.Add(new SQLiteParameter("@name", episodeInfoReturn.EpisodeInfo.Name));
                        addEpisodeCmd.Parameters.Add(new SQLiteParameter("@description", episodeInfoReturn.EpisodeInfo.Description));
                        addEpisodeCmd.Parameters.Add(new SQLiteParameter("@duration", episodeInfoReturn.EpisodeInfo.DurationSecs));
                        addEpisodeCmd.Parameters.Add(new SQLiteParameter("@date", episodeInfoReturn.EpisodeInfo.Date));
                        addEpisodeCmd.Parameters.Add(new SQLiteParameter("@image", this.StoreImage(episodeInfoReturn.EpisodeInfo.Image)));
                        addEpisodeCmd.ExecuteNonQuery();
                    }

                    using (SQLiteCommand getRowIDCmd = new SQLiteCommand("select last_insert_rowid()", FetchDbConn(), transMon.Trans))
                    {
                        epid = (int)(long)getRowIDCmd.ExecuteScalar();
                    }

                    if (episodeInfoReturn.EpisodeInfo.ExtInfo != null)
                    {
                        using (SQLiteCommand addExtInfoCmd = new SQLiteCommand("insert into episodeext (epid, name, value) values (@epid, @name, @value)", FetchDbConn(), transMon.Trans))
                        {
                            foreach (KeyValuePair<string, string> extItem in episodeInfoReturn.EpisodeInfo.ExtInfo)
                            {
                                addExtInfoCmd.Parameters.Add(new SQLiteParameter("@epid", epid));
                                addExtInfoCmd.Parameters.Add(new SQLiteParameter("@name", extItem.Key));
                                addExtInfoCmd.Parameters.Add(new SQLiteParameter("@value", extItem.Value));
                                addExtInfoCmd.ExecuteNonQuery();
                            }
                        }
                    }

                    transMon.Trans.Commit();
                    return epid;
                }
            }
        }

        private void InitEpisodeListThread(int progid)
        {
            Guid providerId = default(Guid);
            string progExtId = null;

            using (SQLiteCommand command = new SQLiteCommand("select pluginid, extid from programmes where progid=@progid", FetchDbConn()))
            {
                command.Parameters.Add(new SQLiteParameter("@progid", progid));

                using (SQLiteMonDataReader reader = new SQLiteMonDataReader(command.ExecuteReader()))
                {
                    if (!reader.Read())
                    {
                        return;
                    }

                    providerId = new Guid(reader.GetString(reader.GetOrdinal("pluginid")));
                    progExtId = reader.GetString(reader.GetOrdinal("extid"));
                }
            }

            List<string> episodeExtIDs = this.GetAvailableEpisodes(providerId, progExtId);

            if (episodeExtIDs != null)
            {
                using (SQLiteCommand findCmd = new SQLiteCommand("select epid from episodes where progid=@progid and extid=@extid", FetchDbConn()))
                {
                    SQLiteParameter progidParam = new SQLiteParameter("@progid");
                    SQLiteParameter extidParam = new SQLiteParameter("@extid");

                    findCmd.Parameters.Add(progidParam);
                    findCmd.Parameters.Add(extidParam);

                    foreach (string episodeExtId in episodeExtIDs)
                    {
                        progidParam.Value = progid;
                        extidParam.Value = episodeExtId;

                        bool needEpInfo = true;
                        int epid = 0;

                        using (SQLiteMonDataReader reader = new SQLiteMonDataReader(findCmd.ExecuteReader()))
                        {
                            if (reader.Read())
                            {
                                needEpInfo = false;
                                epid = reader.GetInt32(reader.GetOrdinal("epid"));
                            }
                        }

                        if (needEpInfo)
                        {
                            epid = this.StoreEpisodeInfo(providerId, progid, progExtId, episodeExtId);

                            if (epid < 0)
                            {
                                continue;
                            }
                        }

                        lock (this.episodeListThreadLock)
                        {
                            if (!object.ReferenceEquals(Thread.CurrentThread, this.episodeListThread))
                            {
                                return;
                            }

                            if (this.EpisodeAdded != null)
                            {
                                this.EpisodeAdded(epid);
                            }
                        }
                    }
                }
            }
        }

        public struct ProviderData
        {
            public string Name;
            public string Description;
            public Bitmap Icon;
            public EventHandler ShowOptionsHandler;
        }
    }
}
