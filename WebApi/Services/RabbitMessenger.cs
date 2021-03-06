﻿using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using SharpRaven;
using SharpRaven.Data;
using System;
using System.Linq;
using System.Text;
using System.Threading;
using WebApi.Database;
using WebApi.Database.Entities;

namespace WebApi.Services
{
    public static class RabbitMessenger
    {
        //private static string QueueOut;
        //private static string QueueIn;

        private static string QueueIn;
        private static string QueueOutBTC;
        private static string QueueOutLTC;

        private static string HostName;
        private static string Exchange;
        private static string SentryUrl;
        private static ConnectionFactory factory;
        private static IModel channel;
        private static IConnection connection;
        private static IBasicProperties properties;
        private static EventingBasicConsumer Consumer;
        private static RavenClient ravenClient;

        public static void Setup(IConfiguration configuration, IHostingEnvironment env)
        {
            string key = env.IsDevelopment() ? "Development" : "Production";
            string UserName = configuration[$"RabbitMQ:{key}:UserName"];
            string Password = configuration[$"RabbitMQ:{key}:Password"];

            //QueueOut = configuration["RabbitMQ:QueueOut"];
            //QueueIn = configuration["RabbitMQ:QueueIn"];

            QueueOutBTC = configuration["RabbitMQ:QueueOutBTC"];
            QueueOutLTC = configuration["RabbitMQ:QueueOutLTC"];
            QueueIn = configuration["RabbitMQ:QueueIn"];

            HostName = configuration[$"RabbitMQ:{key}:HostName"];
            Exchange = configuration["RabbitMQ:Exchange"];
            SentryUrl = configuration["SentryClientUrl"];

            ravenClient = new RavenClient(SentryUrl);

            try
            {
                factory = new ConnectionFactory
                {
                    UserName = UserName,
                    Password = Password,
                    HostName = HostName,
                    Port = 5672
                };

                TryCreateConnection(null, null);
            }
            catch (Exception ex)
            {
                ravenClient.Capture(new SentryEvent(ex));
            }
        }

        private static void TryCreateConnection(object sender, ShutdownEventArgs e)
        {
            try
            {
                CreateConnection();
            }
            catch (Exception)
            {
                Thread.Sleep(2000);
                TryCreateConnection(null, null);
            }
        }

        private static void CreateConnection()
        {
            Connect(null, null);
            connection.ConnectionShutdown += TryCreateConnection;
            channel.ModelShutdown += CreateChannel;

            properties = channel.CreateBasicProperties();
            properties.Persistent = true;

            Consumer = new EventingBasicConsumer(channel);
            Consumer.Received += (ch, ea) =>
            {
                JObject body = JObject.Parse(Encoding.UTF8.GetString(ea.Body));

                //Parse WatchAddress message
                ParseMessage(body);
                channel.BasicAck(ea.DeliveryTag, false);
            };
            String consumerTag = channel.BasicConsume(QueueIn, false, Consumer);
        }

        public static void ParseMessage(JToken message)
        {
            //we need to check if the message is type of rpcReq or rpcResponse
            string method = message["method"].ToString();
            if (!String.IsNullOrEmpty(method))
            {
                switch (method.ToLower())
                {
                    case "setaddress":
                        RabbitMessages.OnSetAddress(message["params"]);
                        break;
                    case "transactionseen":
                        RabbitMessages.OnTransactionSeen(message["params"]);
                        break;
                    case "transactionconfirmed":
                        RabbitMessages.OnTransactionConfirmed(message["params"]);
                        break;
                }
            }
        }

        private static void CreateChannel(object sender, ShutdownEventArgs e)
        {
            try
            {
                channel = connection.CreateModel();

                // Declare queues we are going to use
                channel.QueueDeclare(QueueIn, true, false, false, null);
                channel.QueueDeclare(QueueOutBTC, true, false, false, null);
                channel.QueueDeclare(QueueOutLTC, true, false, false, null);
            }
            catch (Exception ex)
            {
                ravenClient.Capture(new SentryEvent(ex));
            }
        }

        private static void Connect(object sender, ShutdownEventArgs e)
        {
            try
            {
                connection = factory.CreateConnection();
                CreateChannel(null, null);
            }
            catch (Exception ex)
            {
                ravenClient.Capture(new SentryEvent(ex));
                throw ex;
            }
        }

        public static void Send(string message, string queueOut = "")
        {
            Send(new string[] { message }, queueOut);
        }

        public static void Send(string[] messages, string queueOut = "")
        {
            try
            {
                foreach (string message in messages)
                {
                    // Connection is initialized on first request - docker race condition solution
                    if (connection == null || !connection.IsOpen)
                    {
                        CreateConnection();
                    }

                    if (channel.IsClosed)
                    {
                        channel = connection.CreateModel();
                    }

                    byte[] body = Encoding.UTF8.GetBytes(message);
                    if(queueOut != "")
                        channel.BasicPublish("", queueOut, properties, body);
                }
            }
            catch (Exception ex)
            {
                ravenClient.Capture(new SentryEvent(ex));
            }
        }

        public static void Close()
        {
            channel.Close();
            connection.Close(0);
            connection.Dispose();
        }
    }
}
