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
        // indicating if one intent is successfully finished
        bool IntentFin = true;

        // indicating the bot has prompted for food
        // for Calories.Query
        bool AskedForFood = false;

        // flags for Nutri.Query
        // remembering the awaited nutrition or foods
        List<string> CachedNutri = new List<string>();
        List<string> CachedFood = new List<string>();
        // indicating whether is user prompted for nutrition
        static bool AskedForNutri = false;
        static bool AskedForFood2 = false;

        // tracking on previously queried foods
        static List<List<FoodData>> PrevFoods = new List<List<FoodData>>();

        // flags for Diet.Query
        bool Invoked = false;
        // tracking on user age group, set deafult as adult
        static List<DietData> AgeGroupDiet = new List<DietData>();

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
                if (!IntentFin)
                    await AgainIntent(context, result);
                else
                {
                    await context.PostAsync($"{KBNotFound}");
                    context.Wait(MessageReceived);
                }
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

        // selection class for prompting service option
        private enum ServiceOption
        {
            Calories, Nutrition, Recommendation
        }

        // selection class for confirmation
        private enum ConfirmOption
        {
            Yes, No
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
            switch (service)
            {
                case ServiceOption.Calories:
                    stubLR.Intents.Add(new IntentRecommendation("Calories.Query", 1));
                    await CaloriesQueryIntent(context, stubLR);
                    break;

                case ServiceOption.Nutrition:
                    stubLR.Intents.Add(new IntentRecommendation("Nutri.Query", 1));
                    await NutriQueryIntent(context, stubLR);
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
            }
            else //else, prompt user list of serivces only if it is the first greeting, else do not
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

        [LuisIntent("Calories.Query")]
        public async Task CaloriesQueryIntent(IDialogContext context, LuisResult result)
        {
            if (!MatchIntent(result, new string[] { "Again", "None" }))
                ResetFlags();

            IList<string> foods = GetEntities("Food.Name", result);
            IList<string> unknownFoods = new List<string>();
            string reply = "";

            // if the trigger is not from Again intent, update the last intent
            if (!MatchIntent(result, new string[] { "Again", "None" }))
            {
                lastIntent = result.Intents[0].Intent;
            }

            // if food.name is detected in user response
            if (foods.Count > 0)
            {
                IntentFin = true;
                AskedForFood = true;

                IList<FoodData> results = await FoodInfoQuery(foods);

                AddFoods(results);

                for (int i = 0; i < results.Count; i++)
                {
                    if (results[i] != null)
                    {
                        reply += $"{results[i].RowKey} has {results[i].Calories} kcal of calories\n";
                    }
                    else
                    {
                        unknownFoods.Add(foods[i]);
                    }
                }

                //if there is unknown food input by user
                if (unknownFoods.Count > 0)
                {
                    reply += "\nOops, I coudn't find any info on ";
                    foreach (string food in unknownFoods)
                    {
                        reply += $"{food}, ";
                    }
                    reply = reply.Substring(0, reply.Length - 2) + ".";
                }
            }
            // else handling no food match with food.name entity
            else
            {
                // handing follow-up uttereances
                IList<string> FollowUpKey = GetEntities("User.FollowUp", result);
                IList<string> Nutris = GetEntities("Food.Nutri", result);
                if (FollowUpKey.Count > 0 && foods.Count == 0 && 
                    !MatchIntent(lastIntent, new string[] { "Calories.Query" }) && 
                    IntentFin)
                {
                    // handing query on most recent queried foods
                    if (PrevFoods.Count > 0)
                    {
                        List<FoodData> RecentFoods = GetRecentFoods();

                        for (int i = 0; i < RecentFoods.Count; i++)
                        {
                            reply += $"{RecentFoods[i].RowKey} has {RecentFoods[i].Calories} kcal of calories\n";
                        }

                        IntentFin = true;

                    }
                    else // handing there is no food being quried beforehand
                    {
                        IntentFin = false;
                        // asking for food if this is first time user query does not contain foods
                        AskedForFood = true;
                        reply += "Alright, feed us some foods then.";
                    }
                }
                else if (Nutris.Count > 0)
                {
                    await NutriQueryIntent(context, result);
                    return;
                }
                // handing food not found after user being prompted
                else if (AskedForFood)
                {
                    IntentFin = false;
                    // expecting calling back to the same intent after this
                    reply += "Hmm.. We couldn't find any food in your query.\nCan you please try again with other foods?";
                }
                // handling when user firstly invoked this intent
                else
                {
                    IntentFin = false;
                    // asking for food if this is first time user query does not contain foods
                    AskedForFood = true;
                    reply += "Alright, feed us some foods then.";
                }
            }

            await context.PostAsync(reply);
            context.Wait(MessageReceived);
        }

        [LuisIntent("Nutri.Query")]
        public async Task NutriQueryIntent(IDialogContext context, LuisResult result)
        {

            IList<string> foods = GetEntities("Food.Name", result);
            IList<string> nutris = GetEntities("Food.Nutri", result);
            IList<string> unknownFoods = new List<string>();
            string reply = "";

            if (!MatchIntent(result, new string[] { "Again", "None" }))
                ResetFlags();

            // if the trigger is not from Again intent, update the last intent
            if (!MatchIntent(result, new string[] { "Again", "None" }))
            {
                lastIntent = result.Intents[0].Intent;
            }

            // handing complete utterance containing food and nutrition
            // also handing case where user is promted for nutrtion previosuly
            if ((foods.Count > 0 && nutris.Count > 0) || AskedForNutri || AskedForFood2)
            {
                IntentFin = true;
                IList<FoodData> results = new List<FoodData>();

                // if it is a normal complete utterance
                if (foods.Count > 0 && nutris.Count > 0)
                {
                    AskedForNutri = false;
                    AskedForFood2 = false;
                    results = await FoodInfoQuery(foods);
                    AddFoods(results);

                    for (int i = 0; i < results.Count; i++)
                    {
                        if (results[i] != null)
                        {
                            reply += $"{results[i].RowKey} contain\n";
                            for (int j = 0; j < nutris.Count; j++)
                            {
                                reply += $"{GetFoodNutrition(results[i], nutris[j])} grams of {nutris[j]}\n";
                            }
                            reply += "\n";
                        }
                    }
                    reply = reply.Substring(0, reply.Length - 2);

                    // else if user is prompted for nutrition
                }
                else if (AskedForNutri)
                {
                    if (nutris.Count > 0)
                    {
                        AskedForNutri = false;
                        results = await FoodInfoQuery(CachedFood);
                        AddFoods(results);

                        for (int i = 0; i < results.Count; i++)
                        {
                            if (results[i] != null)
                            {
                                reply += $"{results[i].RowKey} contain\n";
                                for (int j = 0; j < nutris.Count; j++)
                                {
                                    reply += $"{GetFoodNutrition(results[i], nutris[j])} grams of {nutris[j]}\n";
                                }
                                reply += "\n";
                            }
                        }
                        reply = reply.Substring(0, reply.Length - 2);

                    }
                    else
                    {
                        reply += "Seems like we didn't spot any valid nutrition. Mind trying again?";
                        IntentFin = false;
                    }

                    // else if user is prompted for foods
                }
                else if (AskedForFood2)
                {
                    if (foods.Count > 0)
                    {
                        AskedForFood2 = false;
                        results = await FoodInfoQuery(foods);
                        AddFoods(results);

                        for (int i = 0; i < results.Count; i++)
                        {
                            if (results[i] != null)
                            {
                                reply += $"{results[i].RowKey} contain\n";
                                for (int j = 0; j < CachedNutri.Count; j++)
                                {
                                    reply += $"{GetFoodNutrition(results[i], CachedNutri[j])} grams of {CachedNutri[j]}\n";
                                }
                                reply += "\n";
                            }
                        }
                        reply = reply.Substring(0, reply.Length - 2);

                    }
                    else
                    {
                        reply += "Hmmm, we don't see any foods. Mind trying again?";
                        IntentFin = false;
                    }

                }
            }
            // handing utterance does not contain either food or nutrition
            else
            {
                // handling followup utterances
                IList<string> FollowUpKey = GetEntities("User.FollowUp", result);
                if (FollowUpKey.Count > 0 && foods.Count == 0 &&
                    nutris.Count > 0 && IntentFin)
                {
                    // handling nutrition query on most recent queried foods
                    if (PrevFoods.Count > 0)
                    {
                        List<FoodData> RecentFoods = GetRecentFoods();
                        for (int i = 0; i < RecentFoods.Count; i++)
                        {
                            reply += $"{RecentFoods[i].RowKey} contain\n";
                            for (int j = 0; j < nutris.Count; j++)
                            {
                                reply += $"{GetFoodNutrition(RecentFoods[i], nutris[j])} grams of {nutris[j]}\n";
                            }
                            reply += "\n";
                        }
                        reply = reply.Substring(0, reply.Length - 2);
                        IntentFin = true;
                        await context.PostAsync(reply);
                        context.Wait(MessageReceived);
                        return;

                    }
                    else
                    {
                        // PASS TO else if (foods.Count == 0 && nutris.Count > 0)
                    }
                }

                IntentFin = false;

                // handing utterance containing no food and nutrition
                if (foods.Count == 0 && nutris.Count == 0)
                {
                    reply += "Sure, feed us some foods.\nYou could also specify nutrition value.";
                }
                // handling utterance containing no food
                else if (foods.Count == 0 && nutris.Count > 0)
                {
                    CachedNutri.Clear();
                    CachedNutri.AddRange(nutris);

                    foreach (string nutri in CachedNutri)
                        reply += $"{nutri}, ";

                    reply = $"{reply.Substring(0, reply.Length - 2)} of which food you want to find out?";
                    AskedForFood2 = true;

                }
                // handling displaying all nutritions info
                else if (foods.Count > 0 && nutris.Count == 0)
                {
                    // remebering foods entered
                    CachedFood.Clear();
                    CachedFood.AddRange(foods);

                    reply += "Displaying all nutrition info for ";
                    foreach (string food in foods)
                    {
                        reply += $"{food}, ";
                    }
                    reply = reply.Substring(0, reply.Length - 2);
                    reply += "?";

                    // confirmation for disaplying all ingo
                    var options = new ConfirmOption[] { ConfirmOption.Yes, ConfirmOption.No };
                    var descs = new string[] { "Yes, all of them.", "Nope, just some specific nutrition." };

                    PromptDialog.Choice<ConfirmOption>(context, ExecDisplayAllNutrition, options, reply, descriptions: descs);
                    return;
                }
                // handing 
                else
                {
                    reply += "You shouln't be seeing this :/";
                }
            }

            await context.PostAsync(reply);
            context.Wait(MessageReceived);
        }

        private async Task ExecDisplayAllNutrition(IDialogContext context, IAwaitable<ConfirmOption> result)
        {
            var option = await result;

            // handling displaying all nutrition information
            if (option == ConfirmOption.Yes)
            {
                string reply = "";
                IList<string> unknownFoods = new List<string>();
                IList<FoodData> results = await FoodInfoQuery(CachedFood);
                AddFoods(results);

                for (int i = 0; i < results.Count; i++)
                {
                    if (results[i] != null)
                    {
                        reply += $"{results[i].RowKey} contains\n{results[i].GetFullNutri()}\n";
                    }
                    else
                    {
                        unknownFoods.Add(CachedFood[i]);
                    }
                }
                reply = reply.Substring(0, reply.Length - 2);

                if (unknownFoods.Count > 0)
                {
                    reply += "Oops, I coudn't find any info on ";
                    foreach (string food in unknownFoods)
                    {
                        reply += $"{food}, ";
                    }
                    reply = reply.Substring(0, reply.Length - 2) + ".";
                }

                IntentFin = true;
                await context.PostAsync(reply);

            }
            else // handing prompting for only specific nutritions
            {
                AskedForNutri = true;
                IntentFin = false;
                await context.PostAsync("Tell us which nutrition you are looking for.");
            }

            context.Wait(MessageReceived);
        }

        [LuisIntent("Diet.Recommend")]
        public async Task DietRecommendIntent(IDialogContext context, LuisResult result)
        {
            await this.ShowLuisResult(context, result);
        }

        [LuisIntent("Diet.Query")]
        public async Task DietQueryIntent(IDialogContext context, LuisResult result)
        {
            //await this.ShowLuisResult(context, result);
            IList<string> foods = GetEntities("Food.Name", result);
            IList<string> nutris = GetEntities("Food.Nutri", result);
            IList<string> group = GetEntities("User.Group", result);

            if (!Invoked)
            {
                Invoked = true;
                DietData buffer = await DietInfoQuery("User.Diet", "adult");
                AgeGroupDiet.Add(buffer);
            }

            string reply = "";
            double ratio = 0.3;

            // handling normal complete utterance
            if (foods.Count > 0 && nutris.Count > 0)
            {
                reply += AgeGroupDiet[0].GetFullDiet();
            }
            else
            {
                reply += "MOM";
            }

            await context.PostAsync(reply);
            context.Wait(MessageReceived);
        }

        [LuisIntent("Symptoms.Food.Query")]
        public async Task SymptomsFoodQueryIntent(IDialogContext context, LuisResult result)
        {

            IList<string> foods = GetEntities("Food.Name", result);
            IList<string> symptoms = GetEntities("User.Symptoms", result);
            IList<FoodData> results = await FoodInfoQuery(foods);
            IList<SymptomsData> symptomsResults = await SymptomsInfoQuery(symptoms);
            
            
           



            //  int[] foodResult = new int[foods.Count];
            String printResultToUser = "";


            for (int i = 0; i < foods.Count; i++)
            {
                int foodGoodCount = 0;
                if (string.Equals(symptoms[0], "constipation"))
                {
                    double carbohydrate = symptomsResults[0].Carbohydrate;
                    double fat = symptomsResults[0].Fat;
                    double fibre = symptomsResults[0].Fibre;
                    double protein = symptomsResults[0].Protein;

                    if (results[i].Carbohydrate >= carbohydrate)
                    {
                        foodGoodCount++;
                    }
                    if (results[i].Fat >= fat)
                    {
                        foodGoodCount++;
                    }
                    if (!(results[i].Fibre <= fibre))
                    {
                        foodGoodCount++;
                    }
                    if (!(results[i].Protein <= protein))
                    {
                        foodGoodCount++;
                    }

                    if (foodGoodCount >= 3)
                        printResultToUser += foods[i] + " is good for constipation person \n";
                    else
                        printResultToUser += foods[i] + " is not good for constipation person \n";
                }
                else if (string.Equals(symptoms[0], "diabetes"))
                {
                    double carbohydrate = symptomsResults[0].Carbohydrate;
                    double sugar = symptomsResults[0].Sugar;

                    if (results[i].Carbohydrate <= carbohydrate)
                    {
                        foodGoodCount++;
                    }
                    if (results[i].Sugar <= sugar)
                    {
                        foodGoodCount++;
                    }
                    if (foodGoodCount >= 2)
                        printResultToUser += foods[i] + " is good for diabetes person \n";
                    else
                        printResultToUser += foods[i] + " is not good for diabetes person \n";



                }
                else
                {
                    double calories = symptomsResults[0].Calories;
                    if (results[i].Calories <= calories)
                    {
                        foodGoodCount++;
                    }
                    if (foodGoodCount >= 1)
                        printResultToUser += foods[i] + " is good for obesity person \n";
                    else
                        printResultToUser += foods[i] + " is not good for obesity person \n";

                }

            }

            await context.PostAsync(printResultToUser);
            context.Wait(MessageReceived);














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
            // resetting all flags and caches
            ResetFlags();

            await context.PostAsync("Alright, we heared you.\nDo you still wanna stay with us?");
            // TODO yes/no option
            context.Wait(MessageReceived);
            //await this.ShowLuisResult(context, result);
        }

        private void ResetFlags()
        {
            IntentFin = true;
            switch (lastIntent)
            {
                case "Calories.Query":
                    AskedForFood = false;
                    break;

                case "Nutri.Query":
                    AskedForNutri = false;
                    AskedForFood2 = false;
                    CachedFood.Clear();
                    CachedNutri.Clear();
                    break;

                default:
                    break;
            }
        }

        [LuisIntent("Again")]
        public async Task AgainIntent(IDialogContext context, LuisResult result)
        {
            switch (lastIntent)
            {

                case "Calories.Query":
                    AskedForFood = true; // in case enter this intent again with utterances containing no food
                    await CaloriesQueryIntent(context, result);
                    break;

                case "Nutri.Query":
                    await NutriQueryIntent(context, result);
                    break;

                case "None":
                default:
                    await context.PostAsync($"AGAIN//{KBNotFound}");
                    context.Wait(MessageReceived);
                    break;
            }
            //await this.ShowLuisResult(context, result);
        }

        // METHODS ON PROCESSING UTTERANCES ===============================

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
            foreach (var ent in result.Entities)
            {
                output += (ent.Type + ' ' + GetResolutionValue(ent).ToLower() + '\n');
            }
            await context.PostAsync($"{output}");
            //await context.PostAsync($"You have reached {result.Intents[0].Intent}. You said: {result.Query}");
            context.Wait(MessageReceived);
        }

        // retrieve list of keys from the luis returned entities by the entity type
        private IList<string> GetEntities(string entity, LuisResult result)
        {
            IList<string> entities = new List<string>();

            foreach (var ent in result.Entities)
            {
                if (string.Compare(entity, ent.Type) == 0)
                {
                    if (!entities.Contains(GetResolutionValue(ent).ToLower()))
                        entities.Add(GetResolutionValue(ent).ToLower());
                }
            }

            return entities;
        }

        private bool MatchIntent(LuisResult result, string[] intents)
        {
            foreach (string intent in intents)
            {
                if (result.Intents[0].Intent.CompareTo(intent) == 0)
                    return true;
            }
            return false;
        }

        private bool MatchIntent(string result, string[] intents)
        {
            foreach (string intent in intents)
            {
                if (result.CompareTo(intent) == 0)
                    return true;
            }
            return false;
        }

        // METHODS RELATED TO DB =======================================

        // append food info objects into query result
        private async Task<IList<FoodData>> FoodInfoQuery(IList<string> foods)
        {

            IList<FoodData> results = new List<FoodData>();

            foreach (var food in foods)
            {

                TableOperation retrieveOp = TableOperation.Retrieve<FoodData>("Food.Name", food);
                TableResult retrievedResult = await foodinfotable.ExecuteAsync(retrieveOp);

                if (retrievedResult.Result != null)
                {
                    results.Add((FoodData)retrievedResult.Result);
                }
                // else, handling food not found in FoodData db
                else
                {
                    results.Add(null);
                }

            }

            return results;

        }

        private async Task<DietData> DietInfoQuery(string PartitionKeyName, string Query)
        {

            TableOperation retrieveOp = TableOperation.Retrieve<DietData>(PartitionKeyName, Query);
            TableResult retrievedResult = await foodinfotable.ExecuteAsync(retrieveOp);

            if (retrievedResult.Result != null)
            {
                return (DietData)retrievedResult.Result;
            }
            // else, handling food not found in FoodData db
            else
            {
                return null;
            }

        }
        private async Task<IList<SymptomsData>> SymptomsInfoQuery(IList<string> symptoms)
        {

            IList<SymptomsData> results = new List<SymptomsData>();

            foreach (var food in symptoms)
            {

                TableOperation retrieveOp = TableOperation.Retrieve<SymptomsData>("Symptoms.Food.Query ", food);
                TableResult retrievedResult = await foodinfotable.ExecuteAsync(retrieveOp);

                if (retrievedResult.Result != null)
                {
                    results.Add((SymptomsData)retrievedResult.Result);
                }
                // else, handling food not found in FoodData db
                else
                {
                    results.Add(null);
                }

            }

            return results;

        }

        // retrieve specific nutrition of a food
        private double GetFoodNutrition(FoodData f, string nutri)
        {
            switch (nutri)
            {
                case "protein":
                    return f.Protein;
                case "fat":
                    return f.Fat;
                case "carbohydrate":
                    return f.Carbohydrate;
                case "sugar":
                    return f.Sugar;
                case "sodium":
                    return f.Sodium;
                case "fibre":
                    return f.Fibre;
                default:
                    return -1;
            }
        }

        // METHODS ON FOOD HISTORY LIST ==================================

        // check if a certain food is previosuly queried
        private bool CheckExists(FoodData f)
        {
            foreach (var foodlist in PrevFoods)
            {
                foreach (var food in foodlist)
                {
                    if (string.Compare(food.RowKey, f.RowKey) == 0)
                        return true;
                }
            }
            return false;
        }

        // add food list into previously queried food list
        private void AddFoods(IList<FoodData> f)
        {
            List<FoodData> food = new List<FoodData>();
            foreach (var s in f)
            {
                // exclude those returned with negative result
                if (s != null)
                    food.Add(s);
            }

            // append only if there is at least one positive result in previous query
            if (food.Count > 0)
                PrevFoods.Add(food);
        }

        // retrieve most recent queried foods
        private List<FoodData> GetRecentFoods()
        {
            return PrevFoods[PrevFoods.Count - 1];
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
                return await response.Content.ReadAsStringAsync();
            }
        }

        public async Task<string> GetAnswer(string question)
        {
            string uri = qnaServiceHostName + "/qnamaker/knowledgebases/" + knowledgeBaseId + "/generateAnswer";
            string questionJSON = "{\"question\": \"" + question.Replace("\"", "'") + "\"}";

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
    public class FoodData : TableEntity
    {

        public FoodData(string domain, string id)
        {
            this.PartitionKey = domain; //ENTITY NAME
            this.RowKey = id; //FOOD NAME, USER GROUP, SYMPTOM NAME
        }

        public FoodData() { }

        public string FoodType { get; set; }
        public double Calories { get; set; }
        public double Fat { get; set; }
        public double Sugar { get; set; }
        public double Sodium { get; set; }
        public double Protein { get; set; }
        public double Carbohydrate { get; set; }
        public double Fibre { get; set; }

        public override string ToString()
        {
            return "FoodType : " + FoodType + "\nFood Name : " + this.RowKey + "\nCalories : " + this.Calories +
                "\nFat : " + this.Fat + "\nSugar : " + this.Sugar + "\nSodium : " + this.Sodium + "\nProtein : " +
                this.Protein + "\nCarbohydrate : " + this.Carbohydrate + "\nFibre : " + this.Fibre;
        }

        public string GetFullNutri()
        {
            return $"Calories: {Calories} kCal\nFat: {Fat} g\nSugar: {Sugar} " +
                $"g\nSodium: {Sodium} g\nProtein: {Protein} g\nCarbohydrate: {Carbohydrate} g\nFibre: {Fibre} g\n";
        }
    }

    // diet table entity class
    public class DietData : TableEntity
    {

        public DietData(string domain, string id)
        {
            this.PartitionKey = domain; //ENTITY NAME
            this.RowKey = id; //FOOD NAME, USER GROUP, SYMPTOM NAME
        }

        public DietData() { }

        public double Calories { get; set; }
        public double Fat { get; set; }
        public double Sugar { get; set; }
        public double Sodium { get; set; }
        public double Protein { get; set; }
        public double Carbohydrate { get; set; }
        public double Fibre { get; set; }

        public override string ToString()
        {
            return "\nFood Name : " + this.RowKey + "\nCalories : " + this.Calories +
                "\nFat : " + this.Fat + "\nSugar : " + this.Sugar + "\nSodium : " + this.Sodium + "\nProtein : " +
                this.Protein + "\nCarbohydrate : " + this.Carbohydrate + "\nFibre : " + this.Fibre;
        }

        public string GetFullDiet()
        {
            return $"Calories: {Calories} kCal\nFat: {Fat} g\nSugar: {Sugar} " +
                $"g\nSodium: {Sodium} g\nProtein: {Protein} g\nCarbohydrate: {Carbohydrate} g\nFibre: {Fibre} g\n";
        }
    }

    // Sysmptoms table entity class
    public class SymptomsData : TableEntity
    {

        public SymptomsData(string domain, string id)
        {
            this.PartitionKey = domain; //ENTITY NAME
            this.RowKey = id; //FOOD NAME, USER GROUP, SYMPTOM NAME
        }

        public SymptomsData() { }

        public double Calories { get; set; }
        public double Fat { get; set; }
        public double Sugar { get; set; }
        public double Sodium { get; set; }
        public double Protein { get; set; }
        public double Carbohydrate { get; set; }
        public double Fibre { get; set; }

        public override string ToString()
        {
            return "\nFood Name : " + this.RowKey + "\nCalories : " + this.Calories +
                "\nFat : " + this.Fat + "\nSugar : " + this.Sugar + "\nSodium : " + this.Sodium + "\nProtein : " +
                this.Protein + "\nCarbohydrate : " + this.Carbohydrate + "\nFibre : " + this.Fibre;
        }

        public string GetFullDiet()
        {
            return $"Calories: {Calories} kCal\nFat: {Fat} g\nSugar: {Sugar} " +
                $"g\nSodium: {Sodium} g\nProtein: {Protein} g\nCarbohydrate: {Carbohydrate} g\nFibre: {Fibre} g\n";
        }
    }
}