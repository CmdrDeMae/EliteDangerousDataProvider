﻿using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Utilities
{
    public class Net
    {
        public static string DownloadString(string uri)
        {
            HttpWebRequest request = GetRequest(uri);
            using (HttpWebResponse response = GetResponse(request))
            {
                if (response == null) // Means that the system was not found
                {
                    return null;
                }

                // Obtain and parse our response
                var encoding = response.CharacterSet == ""
                        ? Encoding.UTF8
                        : Encoding.GetEncoding(response.CharacterSet);

                Logging.Debug("Reading response");
                using (var stream = response.GetResponseStream())
                {
                    var reader = new StreamReader(stream, encoding);
                    string data = reader.ReadToEnd();
                    Logging.Debug("Data is: " + data);
                    return data;
                }
            }
        }

        // Set up a request with the correct parameters for talking to the companion app
        private static HttpWebRequest GetRequest(string url)
        {
            HttpWebRequest request = (HttpWebRequest)HttpWebRequest.Create(url);
            request.Timeout = 10000;
            request.ReadWriteTimeout = 10000;
            return request;
        }

        // Obtain a response, ensuring that we obtain the response's cookies
        private static HttpWebResponse GetResponse(HttpWebRequest request)
        {
            Logging.Debug("Requesting " + request.RequestUri);

            HttpWebResponse response;
            try
            {
                response = (HttpWebResponse)request.GetResponse();
            }
            catch (WebException wex)
            {
                HttpWebResponse errorResponse = wex.Response as HttpWebResponse;
                if (errorResponse.StatusCode == HttpStatusCode.NotFound)
                {
                    // Not found is usual
                    return null;
                }
                else
                {
                    Logging.Warn("Failed to obtain response, error code " + wex.Status);
                    throw wex;
                }
            }
            Logging.Debug("Response is " + JsonConvert.SerializeObject(response));
            return response;
        }

        // Async send a string
        public static void UploadString(string uri, string data)
        {
            Thread thread = new Thread(() =>
            {
                using (var client = new WebClient())
                {
                    try
                    {
                        client.UploadString(uri, data);
                    }
                    catch (Exception ex)
                    {
                        Logging.Warn("Upload of data failed: " + ex);
                    }
                }
            });
            thread.IsBackground = true;
            thread.Start();
        }
    }
}
