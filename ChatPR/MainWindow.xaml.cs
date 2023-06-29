using RestSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Newtonsoft.Json;
using System.Net.Http.Headers;
using System.Net.Http;
using System.IO;
using System.Net;
using System.Threading;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Path = System.IO.Path;
using System.Collections.ObjectModel;
using System.Diagnostics.Eventing.Reader;

namespace ChatPR
{
    /// <summary>
    /// MainWindow.xaml 的交互逻辑
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            GPTAPIKeyPasswordBox.Password = "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";
            GitServerAddressTextBox.Text = "https://code.lotlab.org";
            GitAPIKeyTextBox.Text = "cf2adc30937657e7afcec1410dfbc1fbce5438ce";
            RepoOwnerTextBox.Text = "IGensokyo";
            RepoNameTextBox.Text = "TouhouHeartstone";
            PullRequestTextBox.Text = "328";

            _client = new GPTClient(GPTAPIKeyPasswordBox.Password);

            ReviewButton.Click += ReviewButton_Click;
        }

        private void ReviewButton_Click(object sender, RoutedEventArgs e)
        {
            string giteaServerUrl = GitServerAddressTextBox.Text;
            string apiKey = GitAPIKeyTextBox.Text;
            string repositoryOwner = RepoOwnerTextBox.Text;
            string repositoryName = RepoNameTextBox.Text;
            int pullRequestId = int.Parse(PullRequestTextBox.Text);

            string diff = GetPullRequestDiff(giteaServerUrl, apiKey, repositoryOwner, repositoryName, pullRequestId);

            DiffListView.ItemsSource = _diffListViewDatas;
        }

        public string GetPullRequestDiff(string giteaServerUrl, string apiKey, string repositoryOwner, string repositoryName, int pullRequestId)
        {
            var client = new RestClient(new RestClientOptions(giteaServerUrl));

            var request = new RestRequest($"/api/v1/repos/{repositoryOwner}/{repositoryName}/pulls/{pullRequestId}.diff", Method.Get);
            request.AddHeader("Accept", "application/vnd.gitdiff");
            request.AddHeader("Authorization", $"token {apiKey}");

            var response = client.Execute(request);

            if (response.IsSuccessful)
            {
                return response.Content;
            }
            else
            {
                throw new Exception($"Error fetching PR diff: Status code {response.StatusCode}, Error message {response.ErrorMessage}, Content {response.Content}");
            }
        }

        public async Task ProcessPRDiffAsync(string prUrl, string username, string password, CancellationToken cancellationToken)
        {
            if (!TryParsePrUrl(prUrl, out string gitServerUrl, out string repoOwner, out string repoName, out int prId))
            {
                SetStatusText("Invalid pull request url", Brushes.Red);
                return;
            }

            string url = $"{gitServerUrl}/api/v1/repos/{repoOwner}/{repoName}/pulls/{prId}.diff";

            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.ContentType = "application/vnd.gitdiff";
            request.Headers["Authorization"] = "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{password}"));

            try
            {
                using (WebResponse response = await request.GetResponseAsync())
                using (Stream responseStream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(responseStream, Encoding.UTF8))
                {
                    if (cancellationToken.IsCancellationRequested)
                        return;
                    string line = await reader.ReadLineAsync();
                    do
                    {
                        ProcessPRDiffLine(line);
                    }
                    while (line != null);
                }
            }
            catch (WebException ex)
            {
                using (WebResponse response = ex.Response)
                {
                    HttpWebResponse httpResponse = (HttpWebResponse)response;
                    HttpStatusCode statusCode = httpResponse.StatusCode;

                    using (Stream responseStream = response.GetResponseStream())
                    using (StreamReader reader = new StreamReader(responseStream))
                    {
                        string errorText = reader.ReadToEnd();
                        SetStatusText(string.Format("An error occurred while fetching the PR diff. Please check the error message and try again. Error status:{0}, message:{1}", statusCode, errorText), Brushes.Red);
                        return;
                    }
                }
            }
            catch (IOException ex)
            {
                SetStatusText($"An error occurred while reading the PR diff: {ex.Message}", Brushes.Red);
                return;
            }
        }

        private void SetStatusText(string text, Brush brush)
        {
            StatusTextBlock.Text = text;
            StatusTextBlock.Foreground = brush;
        }

        public void ProcessPRDiffLine(string line)
        {
            if (line == null)
            {
                if (_currentDiffFile != null)
                {
                    EndProcessCurrentFile();
                }
            }
            else if (line.StartsWith("diff --git"))
            {
                // start of file
                if (_currentDiffFile != null)
                {
                    EndProcessCurrentFile();
                }

                var diffLineRegex = new Regex(@"diff --git a/(.*) b/(.*)");
                var match = diffLineRegex.Match(line);

                if (match.Success && !IsIgnoredFileExtension(Path.GetExtension(match.Groups[1].Value)))
                {
                    _currentDiffFile = new DiffFile();
                    _currentDiffFile.OldPath = match.Groups[1].Value;
                    _currentDiffFile.NewPath = match.Groups[2].Value;
                    _diffFiles.Add(_currentDiffFile);
                }
            }
            else if (_currentDiffFile != null)
            {
                if (line.StartsWith("@@"))
                {
                    // start of hunk
                    if (_currentHunk != null)
                    {
                        EndProcessCurrentHunk();
                    }

                    _currentHunk = new DiffHunk();
                    _currentDiffFile.Hunks.Add(_currentHunk);

                    _currentChunk = new DiffChunk();
                }
                else if (_currentHunk != null)
                {
                    if (line.StartsWith("+") || line.StartsWith("-") || line.StartsWith(" "))
                    {
                        int tokenCount = GetTokenCount(line);
                        if (_currentChunk.TokenCount + tokenCount <= _maxChunkTokenCount)
                        {
                            _currentChunk.Lines.Add(line);
                            _currentChunk.TokenCount += tokenCount;
                        }
                        else
                        {
                            _currentHunk.Chunks.Add(_currentChunk);
                            _currentChunk = new DiffChunk();
                        }
                    }
                }
            }
        }

        private bool IsIgnoredFileExtension(string extension)
        {
            return !_reviewFileExtensions.Contains(extension);
        }

        private void EndProcessCurrentFile()
        {
            EndProcessCurrentHunk();

            _currentDiffFile = null;
        }

        private void EndProcessCurrentHunk()
        {
            _currentHunk.Chunks.Add(_currentChunk);
            _currentChunk = null;

            string context = string.Empty;
            // process current hunk
            foreach (var chunk in _currentHunk.Chunks)
            {
                string backgroundDesc = GetBackgroundDesc();
                string codeChanges = string.Concat(chunk.Lines);
                string prompt = string.Format(_userPrompt, backgroundDesc, context, codeChanges);

                context = _client.GetGPTResponse(prompt);
                chunk.Summary = context;
            }

            _currentHunk.Summary = context;
            _currentHunk = null;
        }

        private string GetHunkSummary(List<string> lines)
        {
            string backgroundDesc = GetBackgroundDesc();
            string codeChanges = string.Concat(lines);
            string prompt = string.Format(_userPrompt, backgroundDesc, codeChanges);

            string response = _client.GetGPTResponse(prompt);

            return response;
        }

        private string GetBackgroundDesc()
        {
            //TODO: Impl this method
            return string.Empty;
        }

        private string MergeContext(string prevContext,string context)
        {
            
        }

        private int GetTokenCount(string line)
        {
            return line.Length;
        }

        public static bool TryParsePrUrl(string prUrl, out string gitServerUrl, out string repoOwner, out string repoName, out int prId)
        {
            var match = Regex.Match(prUrl, @"(?<gitServerUrl>(https?|http)://[^/]+|(?:\d{1,3}\.){3}\d{1,3}:\d+)/(?<repoOwner>[^/]+)/(?<repoName>[^/]+)/pulls/(?<prId>\d+)");

            if (match.Success)
            {
                gitServerUrl = match.Groups["gitServerUrl"].Value;
                repoOwner = match.Groups["repoOwner"].Value;
                repoName = match.Groups["repoName"].Value;
                prId = int.Parse(match.Groups["prId"].Value);
                return true;
            }
            else
            {
                gitServerUrl = null;
                repoOwner = null;
                repoName = null;
                prId = -1;
                return false;
            }
        }

        private string[] _reviewFileExtensions = new string[] { };
        private int _maxChunkTokenCount = 2000;

        private List<DiffFile> _diffFiles = new List<DiffFile>();
        private DiffFile _currentDiffFile;
        private DiffHunk _currentHunk;
        private DiffChunk _currentChunk;
        private GPTClient _client;
        private ObservableCollection<DiffListViewData> _diffListViewDatas = new ObservableCollection<DiffListViewData>();

        private readonly string _userPrompt = "你是一个Pull Request代码审查助手，用户会向你提供一些需求描述，上下文概括以及代码变更片段。代码变更片段的格式为：新增的代码行以'+'开头，删除的代码行以'-'开头。未更改的代码行不会有特殊符号。\r\n你需要阅读这些需求描述和代码变更，并按照用户的要求回答问题。\r\n" +
            "\r\n" +
            "需求描述：\r\n" +
            "{0}\r\n" +
            "\r\n" +
            "Git .diff文件格式说明：\r\n" +
            ".diff文件显示了代码中的新增、删除和修改部分。新增的代码行以'+'开头，删除的代码行以'-'开头。未更改的代码行不会有特殊符号。\r\n" +
            "\r\n" +
            "接下来的消息将包含.diff文件的片段。";
    }

    public class DiffFile
    {
        public string OldPath { get; set; }
        public string NewPath { get; set; }
        public List<DiffHunk> Hunks { get; set; } = new List<DiffHunk>();
    }

    public class DiffHunk
    {
        public List<DiffChunk> Chunks { get; set; } = new List<DiffChunk>();
        public string Summary { get; set; }
    }

    public class DiffChunk
    {
        public int TokenCount { get; set; }
        public List<string> Lines { get; set; } = new List<string>();
        public string Summary { get; set; }
    }

    public class DiffListViewData
    {
        public string Title { get; set; }
        public ObservableCollection<HunkListViewData> Hunks { get; set; } = new ObservableCollection<HunkListViewData>();
    }

    public class HunkListViewData
    {
        public string Content { get; set; }
        public string Summary { get; set; }
    }
}
