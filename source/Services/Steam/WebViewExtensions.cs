using Playnite.SDK;
using Playnite.SDK.Events; // Required for WebViewLoadingChangedEventArgs
using System.Threading.Tasks;

namespace FriendsAchievementFeed.Services
{
    public static class WebViewExtensions
    {
        public static async Task NavigateAndWaitAsync(this IWebView webView, string url)
        {
            var tcs = new TaskCompletionSource<bool>();

            // FIX: Use WebViewLoadingChangedEventArgs (not WebViewStateChangedEventArgs)
            void OnLoadingChanged(object sender, WebViewLoadingChangedEventArgs e)
            {
                if (!e.IsLoading)
                {
                    webView.LoadingChanged -= OnLoadingChanged;
                    tcs.TrySetResult(true);
                }
            }

            webView.LoadingChanged += OnLoadingChanged;

            try
            {
                webView.Navigate(url);
                // Wait for the page to load or timeout after 15 seconds
                await Task.WhenAny(tcs.Task, Task.Delay(15000));
            }
            finally
            {
                // Ensure we unsubscribe to prevent memory leaks if the task times out
                webView.LoadingChanged -= OnLoadingChanged;
            }
        }
    }
}