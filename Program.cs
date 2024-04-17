using Tweetinvi;
using Newtonsoft.Json;
using System.IO.Pipes;
using System.Text;
using System.Net.Http.Headers;
using RestSharp;
using RestSharp.Authenticators;
using RestSharp.Authenticators.OAuth;

namespace TecieTwitter
{
    internal class Program
    {
        private static int numThreads = 4;
        static long ChannelId = long.Parse(Environment.GetEnvironmentVariable("TeleChanID") ?? "null");
        static TwitterClient client = null;

        public static Task Main(string[] args) => MainAsync();
        public static async Task MainAsync()
        {
            //start up the bot
            client = new TwitterClient(Environment.GetEnvironmentVariable("TwitterKey"), Environment.GetEnvironmentVariable("TwitterSecret"), Environment.GetEnvironmentVariable("TwitterAccess"), Environment.GetEnvironmentVariable("TwitterAccessKey"));
            var user = client.Users.GetAuthenticatedUserAsync().Result;

            //start up the named pipe for updates
            int i;
            Thread[]? servers = new Thread[numThreads];

            Console.WriteLine("\n*** Tecie Twitter ***\n");
            Console.WriteLine("Server started, waiting for client connect...\n");
            for (i = 0; i < numThreads; i++)
            {
                servers[i] = new(ServerThread);
                servers[i]?.Start();
            }
            Thread.Sleep(250);
            while (i > 0)
            {
                for (int j = 0; j < numThreads; j++)
                {
                    if (servers[j] != null)
                    {
                        if (servers[j]!.Join(50))
                        {
                            Console.WriteLine($"Server thread[{servers[j]!.ManagedThreadId}] finished.");
                            servers[j] = new Thread(ServerThread);
                            servers[j]?.Start();
                        }
                    }
                }
            }
            Console.WriteLine("\nServer threads exhausted, exiting.");
        }

        private static async Task PublishTweet(string message)
        {
            var clientHandler = new HttpClientHandler
            {
                UseCookies = false,
            };
            var httpclient = new HttpClient(clientHandler);
            var request = new HttpRequestMessage
            {
                Method = HttpMethod.Post,
                RequestUri = new Uri("https://api.twitter.com/2/tweets"),
                Headers =
                {
                    { "Authorization", $"OAuth oauth_consumer_key=\"{client.Credentials.ConsumerKey}\", oauth_nonce=\"VIPcj9DZeJBOZkCIcw7rLUA8oGSyWgiX\", oauth_signature=\"0OWFjqgoL4eoOsnyHMbup2nQkE0%3D\", oauth_signature_method=\"HMAC-SHA1\", oauth_timestamp=\"1713321971\", oauth_token=\"{client.Credentials.AccessToken}\", oauth_version=\"1.0\"" },
                },
                Content = new StringContent($"{{\n\t\"text\" : \"{message}\"\n}}")
                {
                    Headers =
                    {
                        ContentType = new MediaTypeHeaderValue("application/json")
                    }
                }
            };
            using (var response = await httpclient.SendAsync(request))
            {
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadAsStringAsync();
                Console.WriteLine(body);
            }
        }

        private static async void ServerThread()
        {
            NamedPipeServerStream pipeServer =
                new NamedPipeServerStream("TecieTwitterPipe", PipeDirection.InOut, numThreads);

            int threadId = Thread.CurrentThread.ManagedThreadId;

            // Wait for a client to connect
            pipeServer.WaitForConnection();

            Console.WriteLine($"Client connected on thread[{threadId}].");
            try
            {
                // Read the request from the client. Once the client has
                // written to the pipe its security token will be available.
                StreamString ss = new StreamString(pipeServer);
                string authkey = Environment.GetEnvironmentVariable("TECKEY") ?? "no key found";

                // Verify our identity to the connected client using a
                // string that the client anticipates.
                if (ss.ReadString() != authkey) { ss.WriteString("Unauthorized client!"); throw new Exception("Unauthorized client connection attemted!"); }
                ss.WriteString(authkey);
                string operation = ss.ReadString(); // E for event ping  A for announcement  U for update

                string post = "";
                ss.WriteString("READY");
                string message = ss.ReadString();

                switch (operation)
                {
                    case "A":
                        post = message;
                        PublishTweet(post).Wait();
                        ss.WriteString("SUCCESS");
                        break;
                    case "E":
                        EventPingInfo eventinfo = JsonConvert.DeserializeObject<EventPingInfo>(message)!;
                        Console.WriteLine(JsonConvert.SerializeObject(eventinfo, Formatting.Indented));
                        post = $"An event is starting!\n\n{eventinfo.EventName}\n\n{(eventinfo.EventLink != null ? $"Join Here! {eventinfo.EventLink}\nSee the current event here: https://thenergeticon.com/Events/currentevent" : "See the current event here: https://thenergeticon.com/Events/currentevent")}";
                        PublishTweet(post).Wait();
                        ss.WriteString("SUCCESS");
                        break;
                    case "U":
                        post = $"Update: {message}";
                        PublishTweet(post).Wait();
                        ss.WriteString("SUCCESS");
                        break;
                    default:
                        Console.WriteLine("Invalid operation");
                        ss.WriteString("FAILURE");
                        break;
                }
            }
            // Catch any exception thrown just in case sumn happens
            catch (Exception e)
            {
                Console.WriteLine($"ERROR: {e.Message}");
            }
            pipeServer.Close();
        }
    }

    class EventPingInfo(string name, string desc, string? link)
    {
        public string EventName = name;
        public string EventDescription = desc;
        public string? EventLink = link;
    }

    public class StreamString
    {
        private Stream ioStream;
        private UnicodeEncoding streamEncoding;

        public StreamString(Stream ioStream)
        {
            this.ioStream = ioStream;
            streamEncoding = new UnicodeEncoding();
        }

        public string ReadString()
        {
            int len = 0;

            len = ioStream.ReadByte() * 256;
            len += ioStream.ReadByte();
            byte[] inBuffer = new byte[len];
            ioStream.Read(inBuffer, 0, len);

            return streamEncoding.GetString(inBuffer);
        }

        public int WriteString(string outString)
        {
            byte[] outBuffer = streamEncoding.GetBytes(outString);
            int len = outBuffer.Length;
            if (len > UInt16.MaxValue)
            {
                len = (int)UInt16.MaxValue;
            }
            ioStream.WriteByte((byte)(len / 256));
            ioStream.WriteByte((byte)(len & 255));
            ioStream.Write(outBuffer, 0, len);
            ioStream.Flush();

            return outBuffer.Length + 2;
        }
    }
}
