using System;
using System.Collections.Generic;
using System.Text;

namespace HttpVideoFrameCaptureModule
{
    public class IPCameraCredential
    {
        public string UserName { get; set; }
        public string Password { get; set; }
    
        public bool IsValid()
        {
            if (!string.IsNullOrEmpty(UserName) && !string.IsNullOrEmpty(Password))
                return true;
            else
                return false;
        }
    }
}