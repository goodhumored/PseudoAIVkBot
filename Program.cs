using MySql.Data.MySqlClient;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using VkNet;

namespace ChatBotVk
{
    internal class Program
    {
        private static Regex botLinkPattern = new Regex(@"^\[club169069226\|(.)+\]\s*", RegexOptions.Multiline);
        private static bool debug;
        private static readonly string[] audioMessageReplies = new string[] {"", "", ""};
        private static readonly string[] oneSecondAudioReplies = new string[] { "", "", ""};
        private static bool working;
        private static readonly VkApi api = new VkApi();
        private static VkNet.Model.LongPollServerResponse longPollServer;
        private static WebRequest longPollRequest;
        private static WebResponse longPollResponse;
        private static string ts;
        private static string key;
        private static bool remember;
        private static readonly MySqlConnection mySqlConn = new MySqlConnection(@"server=localhost;user=root;password=googlovno@gmail.com;");
		
        private static void Main(string[] args)
        {
            Console.Clear();
            Console.WriteLine("Введите AccessToken:");
            var accessToken = Console.ReadLine();
            Console.WriteLine("Загрузка...");
            try
            {
                api.Authorize(new VkNet.Model.ApiAuthParams
                {
                    AccessToken = accessToken
                });
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("Авторизация успешна!");
                Console.ForegroundColor = ConsoleColor.White;
            }
            catch (Exception e)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Авторизация не удалась\nОшибка:{e.Message}");
                Console.ForegroundColor = ConsoleColor.White;
            }
            while (true)
            {
                switch (Console.ReadLine().ToLower())
                {
                    case "старт":
                        working = true;
                        StartAsyncWork();
                        break;
                    case "стоп":
                        working = false;
                        mySqlConn.Close();
                        break;
                    case "выход":
                        mySqlConn.Close();
                        Environment.Exit(0);
                        break;
                    case "пауза":
                        Console.WriteLine("Пауза");
                        break;
                    case "дебаг":
                        if (debug)
                        {
                            debug = false;
                            Console.WriteLine("Дебаг выключен");
                        }
                        else
                        {
                            debug = true;
                            Console.WriteLine("Дебаг включен");
                        }
                        break;
                    case "не запоминай":
                        remember = false;
                        Console.WriteLine("Ок не буду");
                        break;
                    case "запоминай":
                        remember = true;
                        Console.WriteLine("Ок буду");
                        break;
                }
            }
        }
        
        private static async void StartAsyncWork()
        {
            await Task.Run(() => Work());
        }

        private static void Work()
        {
            if (!MysqlConnect() || !LongpollConnect())
                return;
            while (working)
            {
                try
                {
                    #region get response from longpoll server
                    longPollRequest = WebRequest.Create($"{longPollServer.Server}?act=a_check&key={key}&ts={ts}&wait=25");
                    longPollResponse = longPollRequest.GetResponse();
                    var jsonResponse = new StreamReader(longPollResponse.GetResponseStream()).ReadToEnd();
                    var response = JsonConvert.DeserializeObject<Dictionary<string,object>>(jsonResponse);
                    #endregion

                    if (response.ContainsKey("failed"))
                    {
                        if (!SolveLongPollProblem(Convert.ToByte(response["failed"])))
                            return;
                        continue;
                    }

                    var updates = JsonConvert.DeserializeObject<Dictionary<string,object>[]>(response["updates"].ToString()).ToArray();

                    ts = response["ts"] as string;

                    if (updates.Length > 0)
                    {
                        for (int i = 0; i < updates.Length; i++)
                        {
                            string messAttachment;
                            var message = JsonConvert.DeserializeObject<VkNet.Model.Message>(updates[i]["object"].ToString());

                            if (updates[i]["type"].ToString() == "message_edit")
                            {
                                SendMessageAsync(message.PeerId, $"Агаааааа @id{message.FromId} (редачиш))0))");
                            }
                            else if (updates[i]["type"].ToString() == "message_new")
                            {
                                var match = botLinkPattern.Match(message.Text);
                                if (match.Success)
                                    message.Text = message.Text.Replace(match.Value, "");
                                message.Text = ProcessString(message.Text);
                                if (remember)
                                {
                                    if (message.ReplyMessage != null)
                                    {
                                        ProcessReply(message, message.ReplyMessage);
                                    }
                                    else if (message.ForwardedMessages.Count > 0)
                                    {
                                        ProcessReply(message, message.ForwardedMessages[0]);
                                    }
                                }
                                
                                if (message.Attachments.Count > 0)
                                {
                                    messAttachment = GetAttStringByAtt(message.Attachments[0]);
                                    if (messAttachment.Contains("audiomessage"))
                                    {
                                        if ((message.Attachments[0].Instance as VkNet.Model.Attachments.AudioMessage).Duration < 2)
                                            SendMessageAsync(message.PeerId, oneSecondAudioReplies[new Random().Next(audioMessageReplies.Length - 1)]);
                                        else
                                            SendMessageAsync(message.PeerId, audioMessageReplies[new Random().Next(audioMessageReplies.Length - 1)]);
                                        continue;
                                    }
                                }
                                else
                                {
                                    messAttachment = "null";
                                }

                                if (!ProcessCommand(message.Text, message.FromId, message.PeerId))
                                    MakeReply(message.Text, messAttachment, message.PeerId);
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"Ошибка в методе Work: {e.Source}: {e.Message}");
                    Console.ForegroundColor = ConsoleColor.White;
                }
            }
        }

        private static string MakeSqlReqest(string command)
        {
            command = command.Replace("\\'", "\'");
            Console.WriteLine(command);
            MySqlCommand sqlCommand = new MySqlCommand(command, mySqlConn);
            List<string> rows = new List<string>();
            string row = "";
            var reader = sqlCommand.ExecuteReader();
            while (reader.Read())
            {
                for (int col = 0; col < reader.FieldCount; col++)
                {
                    row += reader[col] + "|";
                }
                rows.Add(row.Remove(row.Length - 1));
                row = "";
            }
            reader.Close();
            return String.Join("\n", rows);
        }

        private static bool ProcessCommand(string messageText, long? fromId, long? peerId)
        {
            string[] args;
            Match match1;
            Regex appeal = new Regex(@"^(ии|бот|ии бот)\s*(,)?\s+", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            var match = appeal.Match(messageText);
            if (match.Value != "")
            {
                var command = messageText.Remove(match.Index, match.Length);
                if (debug)
                    Console.WriteLine(command);
                if ((args = command.Split(' '))[0] == "автор")
                    SendMessage(peerId, SelectRequest("replier_id", "replies", $"replier message = '{args[1]}'")[0].ToString());
                bool muted;
                switch (command)
                {
                    case "замолчи":
                        if (fromId == 359317921 || fromId == api.Messages.GetById(new ulong[] { (ulong)peerId.Value }, null)[0].AdminId)
                        {
                            muted = IsMuted(peerId.Value);
                            if (!muted)
                            {
                                SendMessageAsync(peerId, "Ок");
                                Mute(peerId.Value, true);
                            }
                        }
                        else
                            SendMessageAsync(peerId, $"У [id{fromId}|тебя] нет доступа к этой команде");
                        return true;
                    case "можешь говорить":
                        if (fromId == 359317921 || fromId == api.Messages.GetById(new ulong[] { (ulong) peerId.Value},null)[0].AdminId)
                        {
                            muted = IsMuted(peerId.Value);
                            if (muted)
                            {
                                Mute(peerId.Value,false);
                                SendMessageAsync(peerId, "Ура!");
                            }
                            else
                                SendMessageAsync(peerId, "Я и так могу :|");
                        }
                        else
                            SendMessageAsync(peerId, $"У [id{fromId}|тебя] нет доступа к этой команде");
                        return true;
                    case "сколько записей":
                        SendMessageAsync(peerId, new MySqlCommand("SELECT COUNT(*) FROM replies", mySqlConn).ExecuteScalar().ToString());
                        return true;
                    default:
                        return false;
                }
            }
            else if ((args = messageText.Split(' '))[0] == ".mysql")
            {
                if (fromId == 359317921)
                {
                    try
                    {
                        SendMessageAsync(peerId, MakeSqlReqest(messageText.Remove(0, args[0].Length + 1)));
                    }
                    catch (Exception e)
                    {
                        SendMessageAsync(peerId, $"Ошибка: \"{e.Source}:{e.Message}");
                    }
                }
                else
                {
                    SendMessageAsync(peerId, $"У тебя нет доступа к этой команде [id{fromId}|лошара] блин)");
                }
                return true;
            }
            else if ((match1 = new Regex(@"^\.vk\.call", RegexOptions.Singleline | RegexOptions.IgnoreCase).Match(messageText.Replace("\n", "").Replace(" ", ""))).Success)
            {
                if (fromId == 359317921)
                {
                    try
                    {
                        var methodParams = JsonConvert.DeserializeObject<Dictionary<string, string>>(messageText.Remove(match1.Index, match1.Length).Replace(")", ""));
                        var methodName = methodParams["method"];
                        methodParams.Remove("method");
                        var response = api.Call(methodName, new VkNet.Utils.VkParameters(methodParams));
                        SendMessageAsync(peerId, response.RawJson);
                    }
                    catch (Exception e)
                    {
                        SendMessageAsync(peerId, $"Ошибка: \"{e.Source}:{e.Message}");
                    }
                }
                else
                    SendMessageAsync(peerId, $"У [id{fromId}|тебя] нет доступа к этой команде)");
                return true;
            }
            else
                return false;
        }

        private static bool MysqlConnect()
        {
            try
            {
                mySqlConn.Open();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"Подключение к серверу MySql {mySqlConn.DataSource} успешно!");
                Console.ForegroundColor = ConsoleColor.White;
                mySqlConn.ChangeDatabase("replies");
                return true;
            }
            catch(Exception e)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Ошибка в методе MySqlConnect: {e.Source}: {e.Message}");
                Console.ForegroundColor = ConsoleColor.White;
                return false;
            }
        }

        private static bool LongpollConnect() 
		{
            if ((longPollServer = GetLongPollServerResponse()) != null)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("Подключение к LongPoll серверу успешно!");
                Console.ForegroundColor = ConsoleColor.White;
                ts = longPollServer.Ts;
                key = longPollServer.Key;
                return true;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Подключение к LongPoll серверу не увенчалось успехом!");
                Console.ForegroundColor = ConsoleColor.White;
                return false;
            }
        }

        private static bool SolveLongPollProblem(byte errorCode)
        {
            switch (errorCode)
            {
                case 1:
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("История событий устарела или была частично утеряна, приложение может получать события далее, используя новое значение ts из ответа. ");
                    Console.ForegroundColor = ConsoleColor.White;
                    Console.WriteLine("Получаю новый ts");
                    if ((longPollServer = GetLongPollServerResponse()) != null)
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine("Получен новый ts успешно");
                        Console.ForegroundColor = ConsoleColor.White;
                        ts = longPollServer.Ts;
                        return true;
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine("Получить новый ts не вышло");
                        Console.ForegroundColor = ConsoleColor.White;
                        return false;
                    }
                case 2:
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("Истекло время действия ключа, нужно заново получить key методом groups.getLongPollServer.");
                    Console.ForegroundColor = ConsoleColor.White;
                    Console.WriteLine("Получаю новый key");
                    if ((longPollServer = GetLongPollServerResponse()) != null)
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine("Получен новый key успешно");
                        Console.ForegroundColor = ConsoleColor.White;
                        key = longPollServer.Key;
                        return true;
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine("Получить новый ts не вышло");
                        Console.ForegroundColor = ConsoleColor.White;
                        return false;
                    }
                case 3:
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("Информация утрачена, нужно запросить новые key и ts методом groups.getLongPollServer. ");
                    Console.ForegroundColor = ConsoleColor.White;
                    Console.WriteLine("Получаю новые key и ts");
                    if ((longPollServer = GetLongPollServerResponse()) != null)
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine("Получен новые key и ts успешно");
                        Console.ForegroundColor = ConsoleColor.White;
                        ts = longPollServer.Ts;
                        key = longPollServer.Key;
                        return true;
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine("Получить новые key и ts не вышло");
                        Console.ForegroundColor = ConsoleColor.White;
                        return false;
                    }
                default:
                    return false;
            }
        }

        private static void MakeReply(string messageText,string messageAttachment, long? peerId)
        {
            string messageCondition = "message";
            string attachmentCondition = "attachment ";
            if (messageText == "")
            {
                messageCondition += " LIKE '%'";
            }
            else
                messageCondition += $" = '{messageText}'";
            if (messageAttachment == "null")
            {
                attachmentCondition += " LIKE '%'";
            }
            else
                attachmentCondition += $" = '{messageAttachment}'";
            if (messageText == "" && messageAttachment == "null")
                return;
            var results = SelectRequest("replier_message, replier_attachment", "replies", $"{messageCondition} AND {attachmentCondition}");
            if (results != null)
            {
                var att = GettAttByAttString(results[1] as string);
                VkNet.Model.Attachments.MediaAttachment[] atts;
                if (att != null)
                    atts = new VkNet.Model.Attachments.MediaAttachment[] { GettAttByAttString(results[1] as string) };
                else
                    atts = null;
                SendMessageAsync(peerId, results[0] as string, atts);
            }
        }

        private static bool IsMuted(long peerId)
        {
            var isMuted = SelectRequest("is_muted", "mutes", $"peer_id = {peerId}");
            if (isMuted != null)
            {
                if (Convert.ToBoolean(isMuted[0]))
                    return true;
            }
            return false;
        }

        private static void Mute(long peerId, bool value)
        {
            var is_muted = value ? 1 : 0;
            if (SelectRequest("peer_id","mutes",$"peer_id = {peerId}") != null)
            {
                string commandString = $"UPDATE mutes SET is_muted = {is_muted} WHERE peer_id = {peerId};";
                new MySqlCommand(commandString, mySqlConn).ExecuteNonQuery();
            }
            else
            {
                string commandString = $"INSERT INTO mutes (peer_id, is_muted) VALUES ({peerId}, {is_muted});";
                new MySqlCommand(commandString, mySqlConn).ExecuteNonQuery();
            }
        }

        private static void ProcessReply(VkNet.Model.Message message, VkNet.Model.Message reply_message)
        {
            string attachment = "null";
            string replier_attachment = "null";
            string text = "";
            string replier_text = "";
            if (reply_message.Attachments.ToArray().Length > 0)
                attachment = GetAttStringByAtt(reply_message.Attachments.ToArray()[0]);
            if (message.Attachments.ToArray().Length > 0)
                replier_attachment = GetAttStringByAtt(message.Attachments.ToArray()[0]);
            if (!String.IsNullOrEmpty(message.Text))
            {
                IEnumerable<char> chars;
                if (((chars = message.Text.ToLower().Distinct()).Count() <= 3) && chars.Contains('ж') && chars.Contains('ъ'))
                    return;
                else
                    replier_text = message.Text;
            }
            if (!String.IsNullOrEmpty(reply_message.Text))
                text = reply_message.Text;
            var result = SelectRequest("replier_id", "replies", $"replier_attachment = '{replier_attachment}' AND attachment = '{attachment}' AND message = '{text}' AND replier_message = '{replier_text}'");
            if (result == null)
            {
                InsertData((long)message.FromId, reply_message.Text, message.Text, attachment.ToString(), replier_attachment.ToString());
            }
        }

        private static VkNet.Model.Attachments.MediaAttachment GettAttByAttString(string attString)
        {
            string type;
            string ownerId = "";
            string id = "";
            var res = new Regex(@"^[^\d_-]+").Match(attString);
            var ids = attString.Remove(res.Index, res.Length).Split('_');
            type = res.Value;
            if (ids.Length > 1)
            {
                ownerId = ids[0];
                id = ids[1];
            }
            try
            {
                switch (type)
                {
                    case "graffiti":
                        return new VkNet.Model.Attachments.Graffiti
                        {
                            OwnerId = long.Parse(ownerId),
                            Id = long.Parse(id)
                        };
                    case "photo":
                        return new VkNet.Model.Attachments.Photo
                        {
                            OwnerId = long.Parse(ownerId),
                            Id = long.Parse(id)
                        };
                    case "video":
                        return new VkNet.Model.Attachments.Video
                        {
                            OwnerId = long.Parse(ownerId),
                            Id = long.Parse(id)
                        };
                    case "doc":
                        return new VkNet.Model.Attachments.Document
                        {
                            OwnerId = long.Parse(ownerId),
                            Id = long.Parse(id)
                        };
                    case "wall":
                        return new VkNet.Model.Attachments.Wall
                        {
                            OwnerId = long.Parse(ownerId),
                            Id = long.Parse(id)
                        };
                    case "wall_reply":
                        return new VkNet.Model.Attachments.WallReply
                        {
                            OwnerId = long.Parse(ownerId),
                            Id = long.Parse(id)
                        };
                    case "null":
                        return null;
                    case "sticker":
                        return new VkNet.Model.Attachments.Sticker
                        {
                            Id = long.Parse(id)
                        };
                    case "audiomessage":
                        return new VkNet.Model.Attachments.AudioMessage
                        {
                            OwnerId = long.Parse(ownerId),
                            Id = long.Parse(id)
                        };
                    default:
                        throw new Exception($"{type} - неизвестный тип вложения");
                }
            }
            catch (Exception e)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Ошибка: {e.Source}: {e.Message}");
                Console.ForegroundColor = ConsoleColor.White;
                return null;
            }
        }

        private static string GetAttStringByAtt(VkNet.Model.Attachments.Attachment att)
        {
            string type;
            string owner_id;
            string id;
            type = att.Type.Name.ToLower();
            owner_id = att.Instance.OwnerId.ToString();
            id = att.Instance.Id.ToString();
            return $"{type}{owner_id}_{id}";
        }

        private static void InsertData(long replier_id, string message, string replier_message, string attachment, string replier_attachment)
        {
            MySqlCommand command = new MySqlCommand();
            try
            {
                command.Connection = mySqlConn;
                command.CommandText =
                    "INSERT INTO replies " +
                    "(replier_id, " +
                    "message, " +
                    "replier_message, " +
                    "attachment, " +
                    "replier_attachment) " +
                    "VALUES " +
                    $"({replier_id}, " +
                    $"'{message}', " +
                    $"'{replier_message}', " +
                    $"'{attachment}', " +
                    $"'{replier_attachment}');";
                command.ExecuteNonQuery();
                if (debug)
                {
                    Console.WriteLine("Command string = " + command.CommandText);
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("Запись успешно добавлена");
                    Console.ForegroundColor = ConsoleColor.White;
                }
            }
            catch (Exception e)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Ошибка в методе InsertData: {e.Source}: {e.Message}");
                Console.ForegroundColor = ConsoleColor.White;
            }
        }

        private static object[] SelectRequest(string columns, string table, string condition)
        {
            if(debug)
                Console.WriteLine($"Command string = SELECT {columns} FROM {table} WHERE {condition} ORDER BY RAND() LIMIT 1;");
            try
            {
                using (MySqlDataReader reader = new MySqlCommand($"SELECT {columns} FROM {table} WHERE {condition} ORDER BY RAND() LIMIT 1;", mySqlConn).ExecuteReader())
                {
                    if (reader.HasRows)
                    {
                        reader.Read();
                        object[] results = new object[reader.FieldCount];
                        for (int i = 0; i < reader.FieldCount; i++)
                            results[i] = reader.GetValue(i);
                        return results;
                    }
                    return null;
                }
            }
            catch (Exception e)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Ошибка в методе SelectRequest: {e.Source}: {e.Message}");
                Console.ForegroundColor = ConsoleColor.White;
                return null;
            }
        }

        private static VkNet.Model.LongPollServerResponse GetLongPollServerResponse()
        {
            try
            {
                return api.Groups.GetLongPollServer(169069226);
            }
            catch (Exception e)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Подключиться к LongPoll серверу не вышло\nОшибка:{e.Message}");
                Console.ForegroundColor = ConsoleColor.White;
                return null;
            }
        }

        private static void SendMessage(long? peerId, string message, VkNet.Model.Attachments.MediaAttachment[] attachments = null)
        {
            uint? stickerId = 0;
            string[] info;
            if (attachments != null)
            {
                if ((info = attachments[0].ToString().Split('_'))[0] == "sticker")
                {
                    stickerId = uint.Parse(info[1]);
                    attachments = null;
                }
            }
            try
            {
                if (!IsMuted(peerId.Value))
                {
                    if (message.Length > 4096)
                    {
                        int messagesCount = message.Length / 4096;
                        int lastMessageSymbolCount = message.Length % 4096;
                        for (int i = 0; i < messagesCount; i++)
                        {
                            SendMessage(peerId, message.Substring(i * 4096, 4095), attachments);
                        }
                        if (lastMessageSymbolCount > 0)
                            SendMessageAsync(peerId, message.Substring(message.Length - lastMessageSymbolCount));
                    }
                    else
                        api.Messages.Send(new VkNet.Model.RequestParams.MessagesSendParams()
                        {
                            PeerId = peerId,
                            Message = message,
                            Attachments = attachments,
                            StickerId = stickerId,
                            RandomId = new Random().Next(int.MinValue, int.MaxValue)
                        });
                }
            }
            catch (Exception e)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Ошибка в методе SendMessage: {e.Source}: {e.Message}");
                Console.ForegroundColor = ConsoleColor.White;
            }
        }

        private static string ProcessString(string str)
        {
            return str.Replace(@"\", @"\\").Replace("'", @"\'").Replace("\"", "\\\"");
        }

        private static async void SendMessageAsync(long? peerId, string message, VkNet.Model.Attachments.MediaAttachment[] attachments = null)
        {
            uint? stickerId = 0;
            string[] info;
            if (attachments != null)
            {
                if ((info = attachments[0].ToString().Split('_'))[0] == "sticker")
                {
                    stickerId = uint.Parse(info[1]);
                    attachments = null;
                }
            }
            try
            {
                if (!IsMuted(peerId.Value))
                {

                    if (message.Length > 4096)
                    {
                        int messagesCount = message.Length / 4096;
                        int lastMessageSymbolCount = message.Length % 4096;
                        for (int i = 0; i < messagesCount; i++)
                        {
                            SendMessage(peerId, message.Substring(i * 4096, 4095), attachments);
                        }
                        if (lastMessageSymbolCount > 0)
                            SendMessageAsync(peerId, message.Substring(message.Length - lastMessageSymbolCount));
                    }
                    else
                        await api.Messages.SendAsync(new VkNet.Model.RequestParams.MessagesSendParams()
                        {
                            PeerId = peerId,
                            Message = message,
                            Attachments = attachments,
                            StickerId = stickerId,
                            RandomId = new Random().Next(int.MinValue, int.MaxValue)
                        });
                }
            }
            catch (Exception e)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Ошибка в методе SendMessageAsync: {e.Source}: {e.Message}");
                Console.ForegroundColor = ConsoleColor.White;
            }
        }
    }
}