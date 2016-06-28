﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.IO;
using System.Threading;

using slycelote.VsCaide.UI;
using slycelote.VsCaide.Utilities;
using VsInterface;

using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell.Interop;
using EnvDTE;

namespace slycelote.VsCaide
{
    public partial class MainToolWindowControl : System.Windows.Controls.UserControl
    {
        private MainToolWindow mainToolWindow;
        private SynchronizationContext uiCtx;
        private IProjectManager projectManager;

        public MainToolWindowControl(MainToolWindow owner)
        {
            InitializeComponent();
            uiCtx = SynchronizationContext.Current;
            SkipLanguageChangedEvent = true;
            cbProgrammingLanguage.Items.Add("c++");
            cbProgrammingLanguage.Items.Add("c#");
            SkipLanguageChangedEvent = false;
            EnableAll(false);
            this.mainToolWindow = owner;
            this.projectManager = VsVersionDispatcher.GetProjectManager();
        }

        private void ReloadProblemList()
        {
            Logger.Trace("ReloadProblemList");
            try
            {
                EnableFsWatcher(false);
                string stdout = CaideExe.Run("getstate", "core", "problem");
                string currentProblem = stdout == null ? null : stdout.Trim();

                var problemNames = new List<string>();

                if (string.Empty == currentProblem)
                {
                    problemNames.Add("");
                }

                foreach (var subdir in Directory.EnumerateDirectories(SolutionUtilities.GetSolutionDir()))
                {
                    if (Directory.Exists(Path.Combine(subdir, ".caideproblem")) &&
                        File.Exists(Path.Combine(subdir, "problem.ini")))
                    {
                        problemNames.Add(Path.GetFileName(subdir.TrimEnd(Path.DirectorySeparatorChar)));
                    }
                }

                problemNames.Sort(StringComparer.CurrentCultureIgnoreCase);
                cbProblems.Items.Clear();
                foreach (var problem in problemNames)
                {
                    cbProblems.Items.Add(problem);
                }

                if (currentProblem == null)
                {
                    return;
                }

                cbProblems.SelectedItem = currentProblem;
            }
            finally
            {
                EnableFsWatcher(true);
            }
        }

        private string recentFolder = null;

        private void btnCreateSolution_Click(object sender, RoutedEventArgs e)
        {
            if (SolutionUtilities.IsCaideSolution())
            {
                ReloadProblemList();
            }
            else
            {
                string solutionDir = SolutionUtilities.GetSolutionDir();
                bool newSolution = solutionDir == null;
                if (newSolution)
                {
                    var folderBrowserDialog = new FolderBrowserDialog
                    {
                        Description = "Select solution folder",
                        ShowNewFolderButton = true,
                        RootFolder = Environment.SpecialFolder.Desktop,
                        SelectedPath = recentFolder,
                    };
                    var result = folderBrowserDialog.ShowDialog();
                    if (result != DialogResult.OK)
                        return;
                    solutionDir = recentFolder = folderBrowserDialog.SelectedPath;
                }

                if (null == CaideExe.Run(new[] { "init" }, loud: Loudness.LOUD, solutionDir: solutionDir))
                {
                    return;
                }

                if (newSolution)
                {
                    ErrorHandler.ThrowOnFailure(
                        Services.Solution.CreateSolution(solutionDir, "VsCaide", 0)
                    );
                    File.Copy(Path.Combine(solutionDir, "templates", "vs_common.props"),
                              Path.Combine(solutionDir, "vs_common.props"), overwrite: true);
                    SolutionUtilities.SaveSolution();
                }
            }
        }

        private bool IsProjectsLoadingInProgress = false;
        private List<Action> ToDoAfterAllProjectsLoaded = new List<Action>();

        private void AfterProjectsLoaded(Action action)
        {
            bool mustPostpone;
            lock (ToDoAfterAllProjectsLoaded)
            {
                mustPostpone = IsProjectsLoadingInProgress;
                if (mustPostpone)
                    ToDoAfterAllProjectsLoaded.Add(action);
            }
            if (!mustPostpone)
                action();
        }

        public void AllProjects_Loaded()
        {
            lock (ToDoAfterAllProjectsLoaded)
            {
                IsProjectsLoadingInProgress = false;
                ToDoAfterAllProjectsLoaded.ForEach(a => a());
                ToDoAfterAllProjectsLoaded.Clear();
            }
        }

        private CHelperServer CHelperServer;

        public void Solution_Opened()
        {
            lock (ToDoAfterAllProjectsLoaded)
            {
                IsProjectsLoadingInProgress = true;
                ToDoAfterAllProjectsLoaded.Clear();
            }

            bool isCaideDirectory = SolutionUtilities.IsCaideSolution();
            EnableAll(isCaideDirectory);

            if (isCaideDirectory)
            {
                var windowFrame = (IVsWindowFrame)mainToolWindow.Frame;
                windowFrame.Show();
                ReloadProblemList();

                if (CHelperServer != null)
                {
                    CHelperServer.Stop();
                    CHelperServer = null;
                }

                string enableChelperServerStr =
                    CaideExe.Run(new[] { "getopt", "vscaide", "enable_http_server" }, loud: Loudness.QUIET) ?? "1";
                if (new[] { "yes", "1", "true" }.Contains(enableChelperServerStr.ToLowerInvariant().Trim()))
                {
                    CHelperServer = new CHelperServer();
                }
                else
                {
                    Logger.LogMessage("Disabling CHelper HTTP server due to a setting in caide.ini");
                }


                EnableFsWatcher(false);

                string path = Path.Combine(SolutionUtilities.GetSolutionDir(), ".caide");
                fsWatcher = new FileSystemWatcher(path, "config")
                {
                    EnableRaisingEvents = false,
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.LastWrite,
                };
                fsWatcher.Changed += fsWatcher_Changed;
                fsWatcher.EnableRaisingEvents = true;
                AfterProjectsLoaded(() => projectManager.CreateCppLibProject());
            }
        }

        public void Solution_Closed()
        {
            EnableFsWatcher(false);
            fsWatcher = null;

            cbProblems.Items.Clear();
            EnableAll(false);

            if (CHelperServer != null)
            {
                CHelperServer.Stop();
                CHelperServer = null;
            }
        }

        void fsWatcher_Changed(object sender, FileSystemEventArgs e)
        {
            lock (fsWatcherLock)
            {
                if (e.ChangeType != WatcherChangeTypes.Changed || DateTime.Now <= lastChange + TimeSpan.FromSeconds(2))
                    return;
                lastChange = DateTime.Now;
            }
            Logger.Trace("FileChanged");
            uiCtx.Post(_ => AfterProjectsLoaded(ReloadProblemList), null);
        }


        private FileSystemWatcher fsWatcher;
        private DateTime lastChange = DateTime.MinValue;
        private readonly object fsWatcherLock = new object();

        private void EnableFsWatcher(bool enable)
        {
            if (fsWatcher != null)
                fsWatcher.EnableRaisingEvents = enable;
        }

        private void EnableAll(bool enable)
        {
            btnRun.IsEnabled = btnDebug.IsEnabled =
                cbProblems.IsEnabled = cbProgrammingLanguage.IsEnabled = btnEditTests.IsEnabled =
                btnAddNewProblem.IsEnabled = btnParseContest.IsEnabled = btnArchive.IsEnabled = enable;
            btnCreateOrReloadCaideSolution.Content = enable ? "Reload problem list" : "Create caide solution";
        }

        private void cbProblems_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                UpdateCurrentProject();
            }
            catch (Exception ex)
            {
                Logger.LogError("Error: {0}", ex);
            }
        }

        private void UpdateCurrentProject()
        {
            string selectedProblem = cbProblems.SelectedItem as string;
            if (string.IsNullOrEmpty(selectedProblem))
                return;

            try
            {
                if (fsWatcher != null)
                    fsWatcher.EnableRaisingEvents = false;
                if (null == CaideExe.Run("checkout", selectedProblem))
                {
                    return;
                }

                string stdout = CaideExe.Run("probgetstate", selectedProblem, "problem", "language");
                if (null == stdout)
                {
                    return;
                }

                string language = stdout.Trim();
                SetCurrentLanguage(language);

                string[] cppLanguages = new[] { "simplecpp", "cpp", "c++" };
                string[] csLanguages = new[] { "c#", "csharp" };
                if (cppLanguages.Contains(language))
                {
                    AfterProjectsLoaded(() => projectManager.CreateAndActivateCppProject(selectedProblem, language));
                }
                else if (csLanguages.Contains(language))
                {
                    AfterProjectsLoaded(() => projectManager.CreateAndActivateCSharpProject(selectedProblem));
                }
            }
            finally
            {
                if (fsWatcher != null)
                    fsWatcher.EnableRaisingEvents = true;
            }
        }

        private void btnAddNewProblem_Click(object sender, RoutedEventArgs e)
        {
            var problemUrl = PromptDialog.Prompt("Input problem URL or name:", "New problem");
            if (problemUrl == null)
                return;

            if (null == CaideExe.Run(new[] { "problem", problemUrl }, loud: Loudness.LOUD))
            {
                return;
            }
        }

        internal void StartupProject_Changed(IVsHierarchy newStartupProjectHierarchy)
        {
            if (newStartupProjectHierarchy == null)
                return;

            var projectName = SolutionUtilities.GetProject(newStartupProjectHierarchy).Name;
            var currentProblem = (string)cbProblems.SelectedItem;
            if (currentProblem == null || currentProblem.Equals(projectName, StringComparison.CurrentCultureIgnoreCase))
                return;

            if (!IsCaideProblem(projectName))
            {
                // The project doesn't correspond to a problem
                return;
            }

            if (null == CaideExe.Run("checkout", projectName))
            {
                return;
            }
        }

        internal void Project_Removed(IVsHierarchy projectHier)
        {
            Project project = SolutionUtilities.TryGetProject(projectHier);
            if (project == null)
                return;
            var projectName = project.Name;
            if (IsCaideProblem(projectName))
            {
                // Try to mitigate a mysterious error 'Unsatisified (sic!) constraints: folder not empty'.
                System.Threading.Thread.Sleep(500);
                CaideExe.Run("archive", projectName);
                string projectDirectory = Path.Combine(SolutionUtilities.GetSolutionDir(), projectName);
                if (Directory.Exists(projectDirectory))
                    Directory.Delete(projectDirectory, recursive: true);
                SolutionUtilities.SaveSolution();
            }
        }

        private void btnRun_Click(object sender, RoutedEventArgs e)
        {
            Services.DTE.ExecuteCommand("Debug.StartWithoutDebugging");
        }

        private void btnDebug_Click(object sender, RoutedEventArgs e)
        {
            Services.DTE.ExecuteCommand("Debug.Start");
        }

        private void btnParseContest_Click(object sender, RoutedEventArgs e)
        {
            string url = PromptDialog.Prompt("Enter contest URL: ", "Parse contest");
            if (url == null)
                return;
            CaideExe.Run(new[] { "contest", url }, loud: Loudness.LOUD);
        }

        private void btnArchive_Click(object sender, RoutedEventArgs e)
        {
            var currentProblem = (string)cbProblems.SelectedItem;
            if (string.IsNullOrEmpty(currentProblem))
                return;
            var solution = Services.DTE.Solution;
            var project = solution.Projects.OfType<Project>().SingleOrDefault(p => p.Name == currentProblem);
            if (project == null)
            {
                // A problem not tracked by VsCaide
                CaideExe.Run(new[] { "archive", currentProblem }, loud: Loudness.LOUD);
            }
            else
            {
                solution.Remove(project);
                // The problem will be archived on Project_Removed event
            }
        }

        private void btnEditTests_Click(object sender, RoutedEventArgs e)
        {
            string currentProblem = (string)cbProblems.SelectedItem;
            var problemDirectory = Path.Combine(SolutionUtilities.GetSolutionDir(), currentProblem);
            var testCases = TestCase.FromDirectory(problemDirectory);
            testCases = EditTestsWindow.Edit(testCases);
            TestCase.WriteToDirectory(testCases, problemDirectory);
        }

        private bool SkipLanguageChangedEvent = false;
        private void SetCurrentLanguage(string language)
        {
            SkipLanguageChangedEvent = true;
            try
            {
                if (!cbProgrammingLanguage.Items.Contains(language) && !string.IsNullOrEmpty(language))
                    cbProgrammingLanguage.Items.Add(language);
                cbProgrammingLanguage.SelectedItem = language;
            }
            finally
            {
                SkipLanguageChangedEvent = false;
            }
        }

        private void cbProgrammingLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!SkipLanguageChangedEvent)
            {
                var language = (string)cbProgrammingLanguage.SelectedItem;
                if (null == CaideExe.Run(new[] { "lang", language }, loud: Loudness.LOUD))
                {
                    var previousLanguage = (string)e.RemovedItems[0];
                    SetCurrentLanguage(previousLanguage);
                    return;
                }
                UpdateCurrentProject();
            }
        }

        private bool IsCaideProblem(string projectName)
        {
            return cbProblems.Items.Cast<string>().Any(problem =>
                problem.Equals(projectName, StringComparison.CurrentCultureIgnoreCase));
        }
    }
}
