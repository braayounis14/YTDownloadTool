namespace YTDownloadTool.Services
{
    public static class AuthService
    {
        public static bool IsLoggedIn { get; private set; }

        public static void Login()
        {
            IsLoggedIn = true;
        }

        public static void Logout()
        {
            IsLoggedIn = false;
        }
    }
}
