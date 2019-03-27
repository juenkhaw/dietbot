using System;
using System.Configuration;
using System.Threading.Tasks;

// adding libraries for QnAMaker
using System.Net.Http;
using System.Collections.Generic;
using Newtonsoft.Json;
using System.Text;

// adding libraries for AzureTableStorage
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Table;

using Microsoft.Bot.Builder.Dialogs;
using Microsoft.Bot.Builder.Luis;
using Microsoft.Bot.Builder.Luis.Models;

namespace Microsoft.Bot.Sample.LuisBot
{
    // For more information about this template visit http://aka.ms/azurebots-csharp-luis
    [Serializable]
    public class BasicLuisDialog : LuisDialog<object>
    {
        
        // LUIS Settings
        static string LUIS_appId = "d54c7abb-3a29-4cbf-bf20-331dce8240aa";
        static string LUIS_apiKey = "f4a57bb428664071995fc541ce0def61";
        static string LUIS_hostRegion = "westus.api.cognitive.microsoft.com";
    
        // QnA Maker global settings
        // assumes all KBs are created with same Azure service
        static string qnamaker_endpointKey = "e8a88c4b-2866-47d0-818d-333bd8315fa5";
        static string qnamaker_endpointDomain = "dietbotqna";
    
        // QnA Maker knowledge Base setup
        static string domain_kb_id = "5dfa2951-a2a0-43b6-8f1a-2027ee895b37";
    
        // Instantiate the knowledge bases
        public QnAMakerService domainQnAService = new QnAMakerService(
            "https://" + qnamaker_endpointDomain + ".azurewebsites.net", domain_kb_id, qnamaker_endpointKey);
       
        // Preparing Azure table storage     
        static string dbname = "ditebotappdb";
        static string dbkey = "T164DltRhOUy1EbhXonLoxW1G8g8oqe689s/F/jDh8bQzOJy282cjlsBCyyc/TD4GCfVZ4FxK9oMj9sT0zD1Kg==";
        
        // authentication to access a database
        static CloudStorageAccount storeAcc = new CloudStorageAccount(
            new Microsoft.WindowsAzure.Storage.Auth.StorageCredentials(dbname, dbkey), true);
        
        // reference to a database
        static CloudTableClient tableClient = storeAcc.CreateCloudTableClient();
        
        // reference to a table
        static CloudTable foodinfotable = tableClient.GetTableReference("foodinfo");

        // record last used intent
        static string lastIntent = "None";
        
        public BasicLuisDialog() : base(new LuisService(new LuisModelAttribute(
            //ConfigurationManager.AppSettings["LuisAppId"], 
            //ConfigurationManager.AppSettings["LuisAPIKey"], 
            //domain: ConfigurationManager.AppSettings["LuisAPIHostName"])))
            LUIS_appId, LUIS_apiKey, domain: LUIS_hostRegion)))
        {
        }
        
        /**
        private async Task testTable(IDialogContext context) {
            // define operation
            TableOperation retrieveOp = TableOperation.Retrieve<FoodInfo>("food", "apple");
        
            // retrieve result
            TableResult retrievedResult = await foodinfotable.ExecuteAsync(retrieveOp);
            
            // extract result
            if (retrievedResult.Result != null)
                await context.PostAsync($"{((FoodInfo)retrievedResult.Result).toString()}");
            else
                await context.PostAsync("Oops");
        }
        **/

        // handle unknown user utterances
        [LuisIntent("None")]
        public async Task NoneIntent(IDialogContext context, LuisResult result)
        {             
            await context.PostAsync($"Sorry, we couldn't understand you.");
            context.Wait(MessageReceived);
        }

        // Go to https://luis.ai and create a new intent, then train/publish your luis app.
        // Finally replace "Greeting" with the name of your newly created intent in the following handler
        [LuisIntent("Greeting")]
        public async Task GreetingIntent(IDialogContext context, LuisResult result)
        {
            // pass to QnA kb to handle greeting reply
            var qnaMakerAnswer = await domainQnAService.GetAnswer(result.Query);
            await context.PostAsync($"{qnaMakerAnswer}");
            context.Wait(MessageReceived);
        }
        
        [LuisIntent("Bot.Info")]
        public async Task BotInfoIntent(IDialogContext context, LuisResult result)
        {
            // pass to QnA kb to look for related answer and handle help
            var qnaMakerAnswer = await domainQnAService.GetAnswer(result.Query);
            await context.PostAsync($"{qnaMakerAnswer}");
            context.Wait(MessageReceived);
        }
        
        [LuisIntent("Calories.Query")]
        public async Task CaloriesQueryIntent(IDialogContext context, LuisResult result)
        {
            //await this.ShowLuisResult(context, result);
            IList<string> foods = GetEntities("Food.Name", result);
            
            // if food.name is detected in user response
            if(foods.Count > 0) {
                //await context.PostAsync($"Fetching Calories Info...");
                await CaloriesQuery(context, foods);
            }
            // TODO else handling no food.name found in user response

            lastIntent = result.Intents[0].Intent;

            //await this.ShowLuisResult(context, result);
            //context.Wait(MessageReceived);
        }
        
        // append reply with calories query result
        private async Task CaloriesQuery(IDialogContext context, IList<string> foods) {

            // TODO if query for >1 foods
            string reply = "";

            foreach(var food in foods) {
                            
                TableOperation retrieveOp = TableOperation.Retrieve<FoodInfo>("Food.Name", food);
                TableResult retrievedResult = await foodinfotable.ExecuteAsync(retrieveOp);

                if (retrievedResult.Result != null)
                {
                    // TODO adding to history food query

                    reply += $"{food} has {((FoodInfo)retrievedResult.Result).Calories} of calories\n";
                }
                // TODO else, handling food not found
                    
            }
            await context.PostAsync(reply);
            context.Wait(MessageReceived);
        }
        
        [LuisIntent("Nutri.Query")]
        public async Task NutriQueryIntent(IDialogContext context, LuisResult result)
        {
            await this.ShowLuisResult(context, result);
        }
        
        [LuisIntent("Nutri.FullQuery")]
        public async Task NutriFullQueryIntent(IDialogContext context, LuisResult result)
        {
            await this.ShowLuisResult(context, result);
        }
        
        [LuisIntent("Diet.Recommend")]
        public async Task DietRecommendIntent(IDialogContext context, LuisResult result)
        {
            await this.ShowLuisResult(context, result);
        }

        [LuisIntent("Cancel")]
        public async Task CancelIntent(IDialogContext context, LuisResult result)
        {
            await this.ShowLuisResult(context, result);
        }

        [LuisIntent("Again")]
        public async Task AgainIntent(IDialogContext context, LuisResult result)
        {
            switch(lastIntent)
            {
                case "None":
                    await NoneIntent(context, result);
                    break;

                case "Calories.Query":
                    await CaloriesQueryIntent(context, result);
                    break;

                default:
                    await context.PostAsync($"Sorry, we couldn't understand you.");
                    break;
            }
            //await this.ShowLuisResult(context, result);
        }

        private string GetResolutionValue(EntityRecommendation ent)
        {
            var dict = ent.Resolution.Values.GetEnumerator();
            dict.MoveNext();
            var valuesList = (List<object>)dict.Current;
            if (valuesList.Count > 0)
                return (string)valuesList[0];
            else
                return ent.Entity;
        }

        private async Task ShowLuisResult(IDialogContext context, LuisResult result) 
        {
            // default stub to display info of luis result
            string output = result.Intents[0].Intent + '\n';
            foreach(var ent in result.Entities) {
                output += (ent.Type + ' ' + GetResolutionValue(ent).ToLower() + '\n');
            }
            await context.PostAsync($"{output}");
            //await context.PostAsync($"You have reached {result.Intents[0].Intent}. You said: {result.Query}");
            context.Wait(MessageReceived);
        }
        
        // retrieve list of keys from the luis returned entities by the entity type
        private IList<string> GetEntities(string entity, LuisResult result) {
            IList<string> entities = new List<string>();
            
            foreach(var ent in result.Entities) {
                if (string.Compare(entity, ent.Type) == 0)
                {
                    entities.Add(GetResolutionValue(ent).ToLower());
                }
            }
            
            return entities;
        }
    }
    
    // Deserializable class for QnA response
    public class Metadata
    {
        public string name { get; set; }
        public string value { get; set; }
    }
    
    public class Answer
    {
        public IList<string> questions { get; set; }
        public string answer { get; set; }
        public double score { get; set; }
        public int id { get; set; }
        public string source { get; set; }
        public IList<object> keywords { get; set; }
        public IList<Metadata> metadata { get; set; }
    }
    
    public class QnAAnswer
    {
        public IList<Answer> answers { get; set; }
    }
    
    // Http request to QnA Maker service
    [Serializable]
    public class QnAMakerService
    {
        private string qnaServiceHostName;
        private string knowledgeBaseId;
        private string endpointKey;
    
        public QnAMakerService(string hostName, string kbId, string endpointkey)
        {
            qnaServiceHostName = hostName;
            knowledgeBaseId = kbId;
            endpointKey = endpointkey;
    
        }
        
        async Task<string> Post(string uri, string body)
        {
            using (var client = new HttpClient())
            using (var request = new HttpRequestMessage())
            {
                request.Method = HttpMethod.Post;
                request.RequestUri = new Uri(uri);
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                request.Headers.Add("Authorization", "EndpointKey " + endpointKey);
    
                var response = await client.SendAsync(request);
                return  await response.Content.ReadAsStringAsync();
            }
        }
        
        public async Task<string> GetAnswer(string question)
        {
            string uri = qnaServiceHostName + "/qnamaker/knowledgebases/" + knowledgeBaseId + "/generateAnswer";
            string questionJSON = "{\"question\": \"" + question.Replace("\"","'") +  "\"}";
    
            var response = await Post(uri, questionJSON);
    
            var answers = JsonConvert.DeserializeObject<QnAAnswer>(response);
            if (answers.answers.Count > 0)
            {
                return answers.answers[0].answer;
            }
            else
            {
                // meassge to be returned if no good match found
                return "No good match found.";
            }
        }
    }

    // food info entity class 
    public class FoodInfo : TableEntity
    {

        public FoodInfo(string domain, string id)
        {
            this.PartitionKey = domain;
            this.RowKey = id;
        }

        public FoodInfo() { }

        public string FoodType { get; set; }
        public double Calories { get; set; }
        public double Fat { get; set; }
        public double Sugar { get; set; }
        public double Sodium { get; set; }
        public double Protein { get; set; }
        public double Carbohydrate { get; set; }

        public string toString()
        {
            return "FoodType : " + this.FoodType + "\nFood Name : " + this.RowKey + "\nCalories : " + this.Calories + 
                "\nFat : " + this.Fat + "\nSugar : " + this.Sugar + "\nSodium : " + this.Sodium + "\nProtein : " + 
                this.Protein + "\nCarbohydrate : " + this.Carbohydrate;
        }
    }

    // handle list of previously queried food info
    public class FoodHistory
    {

        public IList<FoodInfo> foods;

        FoodHistory()
        {
            foods = new List<FoodInfo>();
        }

        private bool CheckExists(FoodInfo f)
        {
            foreach (var food in foods)
            {
                if (string.Compare(food.RowKey, f.RowKey) == 0)
                    return true;
            }
            return false;
        }

        public void AddFood(FoodInfo f)
        {
            foods.Add(f);
        }

        public FoodInfo GetRecentFood()
        {
            return foods[foods.Count - 1];
        }

        public void ClearHistory()
        {
            foods.Clear();
        }
    }

}