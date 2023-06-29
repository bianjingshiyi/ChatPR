using RestSharp.Authenticators;
using RestSharp;
using System;
using Newtonsoft.Json.Linq;

namespace ChatPR
{
    public class GPTClient
    {
        public GPTClient(string apiKey)
        {
            this.apiKey = apiKey;

            client = new RestClient(new RestClientOptions(BaseUrl) { Authenticator = new JwtAuthenticator(apiKey) });
        }

        public string GetGPTResponse(string prompt)
        {
            var request = new RestRequest("chat/completions", Method.Post);
            request.AddHeader("Content-Type", "application/json");
            request.AddJsonBody(new
            {
                model = "gpt-3.5-turbo",
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = prompt }
                },
                //max_tokens = 50, // 您可以根据需要调整生成的token数量
                //temperature = 0.5f,
            });

            var response = client.Execute(request);
            if (response.IsSuccessful)
            {
                var jsonResponse = JObject.Parse(response.Content);
                var answer = jsonResponse["choices"][0]["message"]["content"].ToString().Trim();
                return answer;
            }
            else
            {
                Console.WriteLine("Error: " + response.ErrorMessage);
                return null;
            }
        }

        private readonly string apiKey;
        private RestClient client;

        private const string BaseUrl = "https://api.openai.com/v1/";
        private const string systemPrompt = "你是一个Pull Request代码审查助手，用户会向你提供一些代码变更片段，并向你提出对代码的变更进行概述，找出里面不符合规范或者可能存在的问题等要求。你要仔细的对代码变更进行检查，并满足用户向你提出的需求。";
    }
}
