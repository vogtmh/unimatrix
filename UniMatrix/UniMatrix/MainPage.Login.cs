using System;
using Windows.UI.Xaml;
using UniMatrix.Services;

namespace UniMatrix
{
    public sealed partial class MainPage
    {
        private async void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            string server = LoginServerBox.Text?.Trim();
            string user = LoginUserBox.Text?.Trim();
            string pass = LoginPassBox.Password;

            if (string.IsNullOrEmpty(server) || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
            {
                ShowLoginError("Please fill in all fields.");
                return;
            }

            SetLoginBusy(true);
            HideLoginError();

            try
            {
                _client.SetHomeserver(server);
                var result = await _client.LoginAsync(user, pass);

                if (string.IsNullOrEmpty(result.AccessToken) || string.IsNullOrEmpty(result.UserId))
                {
                    ShowLoginError("Login failed: no access token returned.");
                    SetLoginBusy(false);
                    return;
                }

                // Persist non-secret settings + token in the credential vault.
                _settings.Homeserver = server;
                _settings.UserId = result.UserId;
                _settings.DeviceId = result.DeviceId;
                _settings.SaveAccessToken(result.UserId, result.AccessToken);

                LoginPassBox.Password = "";

                _syncProcessor = new SyncProcessor(_db, _settings.UserId);
                SetLoginBusy(false);
                ShowView(View.RoomList);
                LoadRoomsFromCache();
                StartSync();
            }
            catch (MatrixException mex)
            {
                ShowLoginError(mex.Message);
                SetLoginBusy(false);
            }
            catch (Exception ex)
            {
                ShowLoginError("Could not reach the homeserver. " + ex.Message);
                SetLoginBusy(false);
            }
        }

        private void SetLoginBusy(bool busy)
        {
            LoginProgress.IsActive = busy;
            LoginButton.IsEnabled = !busy;
            LoginServerBox.IsEnabled = !busy;
            LoginUserBox.IsEnabled = !busy;
            LoginPassBox.IsEnabled = !busy;
        }

        private void ShowLoginError(string message)
        {
            LoginError.Text = message;
            LoginError.Visibility = Visibility.Visible;
        }

        private void HideLoginError()
        {
            LoginError.Visibility = Visibility.Collapsed;
        }
    }
}
