using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace UserService
{
    public class QueueTriggerUserService
    {
        [FunctionName("QueueTriggerUserService")]
        public async Task RunAsync([ServiceBusTrigger("ms-users", Connection = "rasputinServicebus")]string myQueueItem, ILogger log)
        {
            log.LogInformation($"ms-users triggered: {myQueueItem}");
            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();
            DateTime receivedMessageTime = DateTime.UtcNow;
            var message = JsonSerializer.Deserialize<Message>(myQueueItem, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            var logMessage = new Message();
            try {
                List<MessageHeader> headers = new List<MessageHeader>();
                headers.Add(new MessageHeader() { Name = "id-header", Fields = new Dictionary<string, string>() { { "GUID", message.Headers.FirstOrDefault(x => x.Name.Equals("id-header")).Fields["GUID"] } } });
                headers.Add(new MessageHeader() { Name = "current-queue-header", Fields = new Dictionary<string, string>() { { "Name", message.Headers.FirstOrDefault(x => x.Name.Equals("current-queue-header")).Fields["Name"] }, { "Timestamp", message.Headers.FirstOrDefault(x => x.Name.Equals("current-queue-header")).Fields["Timestamp"] } } });
                logMessage.Headers = headers.ToArray();

                var cmd = JsonSerializer.Deserialize<CmdUser>(message.Body, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });
                var user = cmd.User;
                if (cmd.Command == "create")
                {
                    await InsertUserAsync(message, user, log);
                } else if (cmd.Command == "delete")
                {
                    await DeleteUserAsync(message, user, log);
                } else if (cmd.Command == "list")
                {
                    await ListUsersAsync(message, cmd.Parameter, log);
                } else {
                    log.LogError($"Command {cmd.Command} not supported");
                }
                stopwatch.Stop();
                await MessageHelper.SendLog(logMessage, receivedMessageTime, stopwatch.ElapsedMilliseconds);
            } catch(Exception ex) {
                log.LogError("Procesing failed", ex);
                var current = logMessage.Headers.FirstOrDefault(x => x.Name.Equals("current-queue-header"));
                current.Fields["Name"] = current.Fields["Name"] + $"-Error (User): {ex.Message}";
                stopwatch.Stop();
                await MessageHelper.SendLog(logMessage, receivedMessageTime, stopwatch.ElapsedMilliseconds);
            }

        }

        private async Task DeleteUserAsync(Message receivedMessage, Users user, ILogger log)
        {
            var str = Environment.GetEnvironmentVariable("sqldb_connection");
            string query = "DELETE FROM Users WHERE Id = @Id";
            using (SqlConnection connection = new SqlConnection(str))
            {
                connection.Open();
                using (var command = new SqlCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@Id", user.Id);
                    await command.ExecuteNonQueryAsync();
                }
            }
            var message = new Message
            {
                Headers = receivedMessage.Headers,
                Body = JsonSerializer.Serialize(user, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                })
            };
            await MessageHelper.QueueMessageAsync("api-router", message, log);
        }

        private async Task ListUsersAsync(Message receivedMessage, string parameter, ILogger log)
        {
            List<Users> users = new List<Users>();
            var str = Environment.GetEnvironmentVariable("sqldb_connection");
            string query = "SELECT * FROM Users";
            if (parameter != null)
            {
                query += " WHERE id IN (";
                var ids = parameter.Split(',');
                bool first = true;
                for (int i = 0; i < ids.Length; i++)
                {
                    query += (first ? "":",") + "@Id" + i;
                    first = false;
                }
                query += ")";
            }
            using (SqlConnection connection = new SqlConnection(str))
            {
                connection.Open();
                using (SqlCommand command = new SqlCommand(query, connection)) {
                    if (parameter != null)
                    {
                        var ids = parameter.Split(',');
                        for (int i = 0; i < ids.Length; i++)
                        {
                            command.Parameters.AddWithValue("@Id" + i, ids[i]);
                        }
                    }
                    using (SqlDataReader reader = await command.ExecuteReaderAsync()) {
                        while (reader.Read())
                        {
                            var user = new Users
                            {
                                Id = reader.GetInt32(0),
                                Username = reader.GetString(1),
                                Email = reader.GetString(2),
                                Password = reader.GetString(3),
                                CreatedAt = reader.GetDateTime(4)
                            };
                            users.Add(user);   
                        }
                    }
                }
            }
            var message = new Message
            {
                Headers = receivedMessage.Headers,
                Body = JsonSerializer.Serialize(users, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                })
            };
            MessageHelper.AddContentHeader(message, "Users");
            await MessageHelper.QueueMessageAsync("api-router", message, log);
        }

        private async Task InsertUserAsync(Message receivedMessage, Users user, ILogger log)
        {
            var connectionString = Environment.GetEnvironmentVariable("sqldb_connection");
            using (SqlConnection connection = new SqlConnection(connectionString))
            {
                SqlCommand command = new SqlCommand(@"DECLARE @OutputTable TABLE (Id INT);

                    MERGE INTO Users AS target
                    USING(VALUES(@Username, @Email, @Password)) AS source(Username, Email, Password)
                    ON(target.Username = source.Username)
                    WHEN MATCHED THEN
                        UPDATE SET Email = source.Email, Password = source.Password
                    WHEN NOT MATCHED THEN
                        INSERT(Username, Email, Password)
                        VALUES(source.Username, source.Email, source.Password)
                    OUTPUT inserted.Id INTO @OutputTable;

                SELECT Id FROM @OutputTable; ", connection);
                command.Parameters.AddWithValue("@Username", user.Username);
                command.Parameters.AddWithValue("@Email", user.Email);
                command.Parameters.AddWithValue("@Password", user.Password);

                connection.Open();
                int id = Convert.ToInt32(await command.ExecuteScalarAsync());
                connection.Close();

                log.LogInformation($"Inserted user with id {id}");
                user.Id = id;
            }
            var message = new Message
            {
                Headers = receivedMessage.Headers,
                Body = JsonSerializer.Serialize(user, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                })
            };
            MessageHelper.AddContentHeader(message, "User");
            await MessageHelper.QueueMessageAsync("api-router", message, log);
        }
    }
}
