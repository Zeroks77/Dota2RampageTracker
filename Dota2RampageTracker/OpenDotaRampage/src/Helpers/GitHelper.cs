using System;
using System.Diagnostics;
using System.IO;

namespace OpenDotaRampage.Helpers
{
    public static class GitHelper
    {
        public static void CommitAndPush(string repoPath, string playerDirectory, string filePath, string playerName)
        {
            // Ensure the repository is initialized in the correct directory
            if (!Directory.Exists(Path.Combine(repoPath, ".git")))
            {
                RunGitCommand("init", repoPath);
            }

            // Ensure the remote repository is set up
            EnsureRemoteRepository(repoPath);

            // Configure Git to use consistent line endings
            RunGitCommand("config core.autocrlf true", repoPath);

            // Stage the output directory and player directory, excluding error logs
            RunGitCommand("add .", repoPath);
            RunGitCommand("reset *.txt", repoPath);

            // Explicitly add the necessary files
            RunGitCommand($"add \"{filePath}\"", repoPath);
            RunGitCommand($"add \"{Path.Combine(playerDirectory, "LastCheckedMatch.txt")}\"", repoPath);

            // Check for changes
            if (!HasChanges(repoPath))
            {
                Console.WriteLine("No changes to commit.");
                return;
            }

            // Create the commit message
            string timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
            string commitMessage = $"{playerName}-Markdown Rampageupdate-{timestamp}";

            // Create the commit
            RunGitCommand($"commit -m \"{commitMessage}\"", repoPath);

            // Set the upstream branch if not already set
            if (!IsUpstreamSet(repoPath))
            {
                RunGitCommand("push --set-upstream origin master", repoPath);
            }
            else
            {
                // Push the commit to the current repository
                RunGitCommand("push", repoPath);
            }
        }

        private static void EnsureRemoteRepository(string repoPath)
        {
            var gitHubConfig = AppConfigurationController.LoadGitHubConfiguration();
            string repoUrl = gitHubConfig["RepoUrl"];
            string token = Environment.GetEnvironmentVariable("GH_TOKEN");

            // Check if the remote repository is set up
            var processInfo = new ProcessStartInfo("git", "remote -v")
            {
                WorkingDirectory = repoPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (var process = new Process())
            {
                process.StartInfo = processInfo;
                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                if (!output.Contains("origin"))
                {
                    RunGitCommand($"remote add origin https://{token}@{repoUrl}", repoPath);
                }
            }
        }

        private static void RunGitCommand(string command, string workingDirectory)
        {
            var processInfo = new ProcessStartInfo("git", command)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (var process = new Process())
            {
                process.StartInfo = processInfo;
                process.Start();
                process.WaitForExit();

                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();

                Console.WriteLine($"Git command output: {output}");
                Console.WriteLine($"Git command error: {error}");

                // Ignore line ending warnings and handle errors gracefully
                if (process.ExitCode != 0 && !error.Contains("LF will be replaced by CRLF"))
                {
                    Console.WriteLine($"Git command failed: {error}");
                }
            }
        }

        private static bool HasChanges(string repoPath)
        {
            var processInfo = new ProcessStartInfo("git", "status --porcelain")
            {
                WorkingDirectory = repoPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (var process = new Process())
            {
                process.StartInfo = processInfo;
                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                return !string.IsNullOrEmpty(output);
            }
        }

        private static bool IsUpstreamSet(string repoPath)
        {
            var processInfo = new ProcessStartInfo("git", "rev-parse --abbrev-ref --symbolic-full-name @{u}")
            {
                WorkingDirectory = repoPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (var process = new Process())
            {
                process.StartInfo = processInfo;
                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                return process.ExitCode == 0;
            }
        }
    }
}