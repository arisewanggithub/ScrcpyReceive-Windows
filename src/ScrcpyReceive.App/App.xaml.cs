using Microsoft.UI.Xaml;
using System.Text.Json;

namespace ScrcpyReceive.App;

public partial class App : Application
{
    private MainWindow? window;

    public App()
    {
        UnhandledException += (_, args) =>
        {
            var arguments = Environment.GetCommandLineArgs();
            var srt = Array.IndexOf(arguments, "--srt-receiver-self-test");
            if (srt >= 0 && srt + 1 < arguments.Length)
            {
                File.WriteAllText(arguments[srt + 1], JsonSerializer.Serialize(new { passed = false, failure = args.Exception.ToString() }));
                return;
            }
            var index = Array.IndexOf(arguments, "--video-self-test");
            if (index < 0) index = Array.IndexOf(arguments, "--video-backlog-self-test");
            if (index >= 0 && index + 2 < arguments.Length)
                File.WriteAllText(arguments[index + 2], JsonSerializer.Serialize(new
                {
                    passed = false, failure = args.Exception.ToString(), message = args.Message
                }));
            else
            {
                index = Array.IndexOf(arguments, "--self-test");
                if (index >= 0 && index + 1 < arguments.Length)
                    File.WriteAllText(arguments[index + 1], JsonSerializer.Serialize(new
                    {
                        passed = false, failure = args.Exception.ToString(), message = args.Message
                    }));
            }
        };
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var arguments = Environment.GetCommandLineArgs();
        window = new MainWindow(arguments.Any(argument => argument is "--self-test" or "--video-self-test" or "--video-backlog-self-test" or "--srt-receiver-self-test" or "--smoke-test"));
        window.Activate();
        await window.InitializeAsync();
        var testIndex = Array.IndexOf(arguments, "--self-test");
        var videoIndex = Array.IndexOf(arguments, "--video-self-test");
        var srtIndex = Array.IndexOf(arguments, "--srt-receiver-self-test");
        var backlogIndex = Array.IndexOf(arguments, "--video-backlog-self-test");
        if (backlogIndex >= 0)
        {
            if (backlogIndex + 2 >= arguments.Length)
                throw new ArgumentException("Video backlog self-test requires a synthetic fixture and a report file.");
            await window.RunVideoBacklogSelfTestAsync(arguments[backlogIndex + 1], arguments[backlogIndex + 2]);
            window.Close();
        }
        else if (srtIndex >= 0)
        {
            if (srtIndex + 1 >= arguments.Length) throw new ArgumentException("SRT receiver self-test requires a report file.");
            await window.RunSrtReceiverSelfTestAsync(arguments[srtIndex + 1]);
            window.Close();
        }
        else if (videoIndex >= 0)
        {
            if (videoIndex + 2 >= arguments.Length)
                throw new ArgumentException("--video-self-test requires a synthetic fixture and a report file.");
            await window.RunVideoSelfTestAsync(arguments[videoIndex + 1], arguments[videoIndex + 2]);
            window.Close();
        }
        else if (testIndex >= 0)
        {
            if (testIndex + 1 >= arguments.Length)
                throw new ArgumentException("--self-test requires an explicit report file path.");
            await window.RunSelfTestAsync(arguments[testIndex + 1]);
            window.Close();
        }
        else if (arguments.Contains("--smoke-test"))
        {
            var timer = window.DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(3);
            timer.Tick += (_, _) => { timer.Stop(); window.Close(); };
            timer.Start();
        }
    }
}
