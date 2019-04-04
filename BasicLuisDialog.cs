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
        static string qnamaker_endpointKey = "288a1049-9d56-4817-bbf9-3f29d0c2771b";
        static string qnamaker_endpointDomain = "dietbotqnaapp";
    
        // QnA Maker knowledge Base setup
        static string domain_kb_id = "560c0b30-0f41-4049-8800-9c69e4d1cb57";
    
        // Instantiate the knowledge bases
        public QnAMakerService domainQnAService = new QnAMakerService(
            "https://" + qnamaker_endpointDomain + ".azurewebsites.net", domain_kb_id, qnamaker_endpointKey);
       
        // Preparing Azure table storage     
        static string dbname = "dietbotappdb";
        static string dbkey = "9LEblpcDg5GprW8HFW6z7v0bbAnk1R+CEeiW1jdQ1t+Fz1QCMkX2OK6UstjFuqjjqL4/EHyx13UVkv0mPOlidQ==";
        
        // authentication to access a database
        static CloudStorageAccount storeAcc = new CloudStorageAccount(
            new Microsoft.WindowsAzure.Storage.Auth.StorageCredentials(dbname, dbkey), true);
        
        // reference to a database
        static CloudTableClient tableClient = storeAcc.CreateCloudTableClient();
        
        // reference to a table
        static CloudTable foodinfotable = tableClient.GetTableReference("foodinfo");

        // FLAGS SECTION==========================================================
        // record last used intent
        string lastIntent = "None";

        // error message in case of qna kb match not found
        static string KBOriginalNotFound = "No good match found in KB.";
        static string KBNotFound = "Sorry, we couldn't understand you.";

        // indicating the bot has started
        bool SessionStarted = false;

        // indicating the bot has prompted for food
        // for Calories.Query
        bool AskedForFood = false;
        // ========================================================================

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

        // handle unidentified user intention
        // assumming user asking about the bot, else bot say sorry
        [LuisIntent("None")]
        public async Task NoneIntent(IDialogContext context, LuisResult result)
        {
            // pass to QnA kb to look for related answer and handle help
            var qnaMakerAnswer = await domainQnAService.GetAnswer(result.Query);

            if (qnaMakerAnswer.CompareTo(KBOriginalNotFound) == 0)
            {
                await AgainIntent(context, result);
                //await context.PostAsync($"{KBNotFound}");
            }
            else
            {
                await context.PostAsync($"{qnaMakerAnswer}");
                context.Wait(MessageReceived);
            }
        }
        
        // handle user acknowledgement
        [LuisIntent("User.Acknowledge")]
        public async Task UserAcknowledgeIntent(IDialogContext context, LuisResult result)
        {
            await context.PostAsync($"That's good. Anything else you want to ask us?");
            context.Wait(MessageReceived);
        }

        // slection class for prompting service option
        private enum ServiceOption
        {
            Calories, Nutrition, Recommendation
        }

        [LuisIntent("Bot.Service")]
        public async Task BotServiceIntent(IDialogContext context, LuisResult result)
        {
            //reset flag on calories query
            AskedForFood = false;

            await ListServiceOption(context, "You could start with one of these:");
        }

        private async Task ListServiceOption(IDialogContext context, string msg)
        {
            var options = new ServiceOption[] { ServiceOption.Calories, ServiceOption.Nutrition, ServiceOption.Recommendation };
            var descs = new string[] { "How much calories have I consumed?", "What's nutrition of that food?", "Recommend me a meal." };

            PromptDialog.Choice<ServiceOption>(context, ExecServiceOption, options, msg, descriptions: descs);
        }

        private async Task ExecServiceOption(IDialogContext context, IAwaitable<ServiceOption> result)
        {
            var service = await result;
            LuisResult stubLR = new LuisResult("", new List<EntityRecommendation>(), new IntentRecommendation(), 
                new List<IntentRecommendation>(), new List<CompositeEntity>());
            // TODO pass to respective Intent
            switch(service)
            {
                case ServiceOption.Calories:
                    stubLR.Intents.Add(new IntentRecommendation("Calories.Query", 1));
                    await CaloriesQueryIntent(context, stubLR);
                    break;

                default:
                    break;
            }
            //await context.PostAsync($"You chose {service}");
            //context.Wait(MessageReceived);
        }

        // Go to https://luis.ai and create a new intent, then train/publish your luis app.
        // Finally replace "Greeting" with the name of your newly created intent in the following handler
        [LuisIntent("Greeting")]
        public async Task GreetingIntent(IDialogContext context, LuisResult result)
        {
            // pass to QnA kb to handle greeting reply
            var qnaMakerAnswer = await domainQnAService.GetAnswer(result.Query);

            // if this is a miss in KB
            if (qnaMakerAnswer.CompareTo(KBOriginalNotFound) == 0)
            {
                await context.PostAsync($"GREET//{KBNotFound}");
                context.Wait(MessageReceived);
            } else //else, prompt user list of serivces only if it is the first greeting, else do not
            {
                if (!SessionStarted)
                {
                    // marks session started and show list of options
                    SessionStarted = true;
                    await ListServiceOption(context, qnaMakerAnswer);
                }
                else
                {
                    // if greeting is done after session started
                    await context.PostAsync($"Yes? We are still up here.");
                    context.Wait(MessageReceived);
                }

            }
        }

        [LuisIntent("User.Aye")]
        public async Task AyeIntent(IDialogContext context, LuisResult result)
        {
            await context.PostAsync($"You said YES!");
            context.Wait(MessageReceived);
        }

        [LuisIntent("User.Nay")]
        public async Task NayIntent(IDialogContext context, LuisResult result)
        {
            await context.PostAsync($"You said NO!");
            context.Wait(MessageReceived);
        }

        [LuisIntent("Calories.Query")]
        public async Task CaloriesQueryIntent(IDialogContext context, LuisResult result)
        {
            //await this.ShowLuisResult(context, result);
            if (result.Intents[0].Intent.CompareTo("Calories.Query") == 0)
            {
                AskedForFood = false;
            }

            IList<string> foods = GetEntities("Food.Name", result);
            IList<string> unknownFoods = new List<string>();
            string reply = "";
            
            // if food.name is detected in user response
            if (foods.Count > 0) {

                IList<double> calories = await CaloriesQuery(foods);
                for(int i = 0; i < calories.Count; i++)
                {
                    if (calories[i] >= 0)
                    {
                        reply += $"{foods[i]} has {calories[i]} kcal of calories\n";
                    }   
                    else
                    {
                        unknownFoods.Add(foods[i]);
                    }
                }

                //if there is unknown food input by user
                if(unknownFoods.Count > 0)
                {
                    reply += "\nOops, I coudn't find any info on ";
                    foreach(string food in unknownFoods)
                    {
                        reply += $"{food}, ";
                    }
                    reply = reply.Substring(0, reply.Length - 2) + ".";
                }
            }
            // else handling no food match with food.name entity
            else
            {
                if (AskedForFood)
                {
                    // expecting calling back to the same intent after this
                    reply += "Hmm.. We couldn't find any food in your query.\nCan you please try again with other foods?";
                } else
                {
                    // asking for food if this is first time user query does not contain foods
                    AskedForFood = true;
                    reply += "Alright, feed us some foods then.";
                }
            }

            // if the trigger is not from Again intent, update the last intent
            if (result.Intents[0].Intent.CompareTo("Again") != 0 && result.Intents[0].Intent.CompareTo("None") != 0)
            {
                lastIntent = result.Intents[0].Intent;
            }

            await context.PostAsync(reply);
            context.Wait(MessageReceived);
        }
        
        // append reply with calories query result
        private async Task<IList<double>> CaloriesQuery(IList<string> foods) {

            IList<double> calories = new List<double>();

            foreach (var food in foods) {
                            
                TableOperation retrieveOp = TableOperation.Retrieve<FoodInfo>("Food.Name", food);
                TableResult retrievedResult = await foodinfotable.ExecuteAsync(retrieveOp);

                if (retrievedResult.Result != null)
                {
                    // TODO adding to history food query

                    calories.Add(((FoodInfo)retrievedResult.Result).Calories);
                }
                // else, handling food not found in foodinfo db
                else
                {
                    calories.Add(-1);
                }
                    
            }

            return calories;
            
        }
        
        [LuisIntent("Nutri.Query")]
        public async Task NutriQueryIntent(IDialogContext context, LuisResult result)
        {
            //await this.ShowLuisResult(context, result);
            // TODO also to be coped with Nutri.FullQuery
        }
        
        [LuisIntent("Diet.Recommend")]
        public async Task DietRecommendIntent(IDialogContext context, LuisResult result)
        {
            await this.ShowLuisResult(context, result);
        }

        [LuisIntent("Diet.Query")]
        public async Task DietQueryIntent(IDialogContext context, LuisResult result)
        {
            await this.ShowLuisResult(context, result);
        }

        [LuisIntent("Symptoms.Food.Query")]
        public async Task SymptomsFoodQueryIntent(IDialogContext context, LuisResult result)
        {
            await this.ShowLuisResult(context, result);
        }

        [LuisIntent("Finish")]
        public async Task FinishIntent(IDialogContext context, LuisResult result)
        {
            // pass to QnA kb to look for related answer and handle help
            var qnaMakerAnswer = await domainQnAService.GetAnswer(result.Query);

            if (qnaMakerAnswer.CompareTo(KBOriginalNotFound) == 0)
            {
                await context.PostAsync($"FIN//{KBNotFound}");
            }
            else
            {
                await context.PostAsync($"{qnaMakerAnswer}");
            }
            context.Wait(MessageReceived);
        }

        [LuisIntent("Cancel")]
        public async Task CancelIntent(IDialogContext context, LuisResult result)
        {
            //lastIntent = null;
            await context.PostAsync("Alright, we heared you.\nDo you still wanna stay with us?");
            // TODO yes/no option
            context.Wait(MessageReceived);
            //await this.ShowLuisResult(context, result);
        }

        [LuisIntent("Again")]
        public async Task AgainIntent(IDialogContext context, LuisResult result)
        {
            switch(lastIntent)
            {

                case "Calories.Query":
                    AskedForFood = true; // in case enter this intent again with utterances containing no food
                    await CaloriesQueryIntent(context, result);
                    break;

                case "None":
                default:
                    await context.PostAsync($"AGAIN//{KBNotFound}");
                    context.Wait(MessageReceived);
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
        // TODO add fibre

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