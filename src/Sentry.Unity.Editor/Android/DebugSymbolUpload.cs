using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Sentry.Extensibility;
using Sentry.Unity.Integrations;
using UnityEditor;

namespace Sentry.Unity.Editor.Android
{
    internal class DebugSymbolUpload
    {
        private readonly IDiagnosticLogger _logger;

        internal const string RelativeBuildOutputPathOld = "Temp/StagingArea/symbols";
        internal const string RelativeBuildOutputPathOldMono = "Temp/StagingArea/symbols";
        internal const string RelativeGradlePathOld = "Temp/gradleOut";
        internal const string RelativeBuildOutputPathNew = "Library/Bee/artifacts/Android";
        internal const string RelativeAndroidPathNew = "Library/Bee/Android";

        private readonly string _unityProjectPath;
        private readonly string _gradleProjectPath;
        private readonly string _gradleScriptPath;
        private readonly ScriptingImplementation _scriptingBackend;

        private readonly SentryCliOptions? _cliOptions;
        internal string[] _symbolUploadPaths;

        private const string SymbolUploadTaskStartComment = "// Autogenerated Sentry symbol upload task [start]";
        private const string SymbolUploadTaskEndComment = "// Autogenerated Sentry symbol upload task [end]";

        private string _symbolUploadTask = @"
// Credentials and project settings information are stored in the sentry.properties file
gradle.taskGraph.whenReady {{
    gradle.taskGraph.allTasks[-1].doLast {{
        println 'Uploading symbols to Sentry. You can find the full log in ./Logs/sentry-symbols-upload.log (the file content may not be strictly sequential because it\'s a merge of two streams).'
        def sentryLogFile = new FileOutputStream('{2}/Logs/sentry-symbols-upload.log')
        exec {{
            environment 'SENTRY_PROPERTIES', './sentry.properties'
            executable '{0}'
            args = ['upload-dif', {1}]
            standardOutput sentryLogFile
            errorOutput sentryLogFile
        }}
    }}
}}";

        public DebugSymbolUpload(IDiagnosticLogger logger,
            SentryCliOptions? cliOptions,
            string unityProjectPath,
            string gradleProjectPath,
            ScriptingImplementation scriptingBackend,
            bool isExporting = false,
            IApplication? application = null)
        {
            _logger = logger;

            _unityProjectPath = unityProjectPath;
            _gradleProjectPath = gradleProjectPath;
            _gradleScriptPath = Path.Combine(_gradleProjectPath, "build.gradle");
            _scriptingBackend = scriptingBackend;

            _cliOptions = cliOptions;
            _symbolUploadPaths = GetSymbolUploadPaths(isExporting, application);
        }

        public void AppendUploadToGradleFile(string sentryCliPath)
        {
            if (LoadGradleScript().Contains("sentry.properties"))
            {
                _logger.LogDebug("Symbol upload has already been added in a previous build.");
                return;
            }

            _logger.LogInfo("Appending debug symbols upload task to gradle file.");

            sentryCliPath = ConvertSlashes(sentryCliPath);
            if (!File.Exists(sentryCliPath))
            {
                throw new FileNotFoundException("Failed to find sentry-cli", sentryCliPath);
            }

            var uploadDifArguments = "\"--il2cpp-mapping\",";
            if (_cliOptions?.UploadSources ?? false)
            {
                uploadDifArguments += "\"--include-sources\",";
            }

            foreach (var symbolUploadPath in _symbolUploadPaths)
            {
                if (Directory.Exists(symbolUploadPath))
                {
                    uploadDifArguments += $"\"{ConvertSlashes(symbolUploadPath)}\",";
                }
                else
                {
                    throw new DirectoryNotFoundException($"Failed to find the symbols directory at {symbolUploadPath}");
                }
            }

            using var streamWriter = File.AppendText(_gradleScriptPath);
            streamWriter.WriteLine(SymbolUploadTaskStartComment);
            streamWriter.WriteLine(_symbolUploadTask.Trim(), sentryCliPath, uploadDifArguments, ConvertSlashes(_unityProjectPath));
            streamWriter.WriteLine(SymbolUploadTaskEndComment);
        }

        private string LoadGradleScript()
        {
            if (!File.Exists(_gradleScriptPath))
            {
                throw new FileNotFoundException($"Failed to find the gradle config.", _gradleScriptPath);
            }
            return File.ReadAllText(_gradleScriptPath);
        }

        public void RemoveUploadFromGradleFile()
        {
            _logger.LogDebug("Removing the upload task from the gradle project.");
            var gradleBuildFile = LoadGradleScript();
            if (!gradleBuildFile.Contains("sentry.properties"))
            {
                _logger.LogDebug("No previous upload task found.");
                return;
            }

            var regex = new Regex(Regex.Escape(SymbolUploadTaskStartComment) + ".*" + Regex.Escape(SymbolUploadTaskEndComment), RegexOptions.Singleline);
            gradleBuildFile = regex.Replace(gradleBuildFile, "");

            using var streamWriter = File.CreateText(_gradleScriptPath);
            streamWriter.Write(gradleBuildFile);
        }

        public void TryCopySymbolsToGradleProject(IApplication? application = null)
        {
            // The new building backend takes care of making the debug symbol files available within the exported project
            if (IsNewBuildingBackend(application))
            {
                _logger.LogDebug("New building backend. Skipping copying of debug symbols.");
                return;
            }

            _logger.LogInfo("Copying debug symbols to exported gradle project.");

            var buildOutputPath = Path.Combine(_unityProjectPath, RelativeBuildOutputPathOld);
            var targetRoot = Path.Combine(_gradleProjectPath, "symbols");

            foreach (var sourcePath in Directory.GetFiles(buildOutputPath, "*.so", SearchOption.AllDirectories))
            {
                var targetPath = sourcePath.Replace(buildOutputPath, targetRoot);
                _logger.LogDebug("Copying '{0}' to '{1}'", sourcePath, targetPath);

                Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
                FileUtil.CopyFileOrDirectory(sourcePath, targetPath);
            }
        }

        internal string[] GetSymbolUploadPaths(bool isExporting, IApplication? application = null)
        {
            if (isExporting)
            {
                _logger.LogInfo("Exporting the project. Root for symbols upload: {0}", _gradleProjectPath);
                return new[] { _gradleProjectPath };
            }

            var paths = new List<string>();
            if (IsNewBuildingBackend(application))
            {
                _logger.LogInfo("Unity version 2021.2 or newer detected. Root for symbols upload: 'Library'.");
                if (_scriptingBackend == ScriptingImplementation.IL2CPP)
                {
                    paths.Add(Path.Combine(_unityProjectPath, RelativeBuildOutputPathNew));
                }
                paths.Add(Path.Combine(_unityProjectPath, RelativeAndroidPathNew));
            }
            else
            {
                _logger.LogInfo("Unity version 2021.1 or older detected. Root for symbols upload: 'Temp'.");
                if (_scriptingBackend == ScriptingImplementation.IL2CPP)
                {
                    paths.Add(Path.Combine(_unityProjectPath, RelativeBuildOutputPathOld));
                }
                paths.Add(Path.Combine(_unityProjectPath, RelativeGradlePathOld));
            }
            return paths.ToArray();
        }

        // Starting from 2021.2 Unity caches the build output inside 'Library' instead of 'Temp'
        internal static bool IsNewBuildingBackend(IApplication? application = null) => SentryUnityVersion.IsNewerOrEqualThan("2021.2", application);

        // Gradle doesn't support backslashes on path (Windows) so converting to forward slashes
        internal static string ConvertSlashes(string path) => path.Replace(@"\", "/");
    }
}
