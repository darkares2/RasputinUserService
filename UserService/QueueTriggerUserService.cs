using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Azure.Storage;
using Microsoft.Azure.Storage.Queue;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace UserService
{
    public class QueueTriggerUserService
    {
        [FunctionName("QueueTriggerUserService")]
        public async Task RunAsync([QueueTrigger("ms-users", Connection = "rasputinstorageaccount_STORAGE")]string myQueueItem, ILogger log)
        {
            log.LogInformation($"C# Queue trigger function processed: {myQueueItem}");
            var message = JsonSerializer.Deserialize<Message>(myQueueItem, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            var cmd = JsonSerializer.Deserialize<CmdUser>(message.Body, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            var user = cmd.User;
            if (cmd.Command == "create")
            {
                await InsertUserAsync(message, user, log);
            } else if (cmd.Command == "list")
            {
                await ListUsersAsync(message, cmd.Parameter, log);
            } else {
                log.LogError($"Command {cmd.Command} not supported");
            }

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
                SqlCommand command = new SqlCommand("INSERT INTO users (username, email, password) VALUES (@Username, @Email, @Password); SELECT SCOPE_IDENTITY();", connection);
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
