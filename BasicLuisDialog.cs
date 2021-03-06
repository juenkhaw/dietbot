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
        static string LUIS_apiKey = "fe905b5bda634933879d89355da2dded";
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
        bool CalledCaloriesOptions = false;

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
        static DietData AgeGroupDiet = new DietData();
        List<string> CachedNutri2 = new List<string>();

        // flags for Diet.Recommend
        bool AskedForFood3 = false;
        bool ReloadedPrevFoods = false;

        // fixed mapping food type <--> nutrition
        static Dictionary<string, List<string>> FoodTypeNutriMap = new Dictionary<string, List<string>>
        {
            ["fruits"] = new List<string> { "fibre", "sugar", "carbohydrate" },
            ["vegetables"] = new List<string> { "fibre", "sodium" },
            ["grains"] = new List<string> { "carbohydrate" },
            ["protein food"] = new List<string> { "protein", "sodium" },
            ["dairy product"] = new List<string> { "sugar", "fat", "carbohydrate" },
            ["oils product"]  = new List<string> { "fat" }
        };

        // fixed mapping food type <--> food name
        /*
        static Dictionary<string, List<string>> FoodTypeNameMap = new Dictionary<string, List<string>>
        {
            ["fruits"] = new List<string> { "banana", "apple" },
            ["vegetables"] = new List<string> { "broccoli", "carrot" },
            ["protein"] = new List<string> { "fish", "chicken" },
            ["grains"] = new List<string> { "bread", "rice" },
            ["oils"] = new List<string> { "crackers", "butter" },
            ["dairy"] = new List<string> { "milk", "yogurt" }
        };*/

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
        private enum ConfirmOption {
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
            // pass to respective Intent
            switch (service)
            {
                case ServiceOption.Calories:
                    CalledCaloriesOptions = true;
                    stubLR.Intents.Add(new IntentRecommendation("Calories.Query", 1));
                    await CaloriesQueryIntent(context, stubLR);
                    break;

                case ServiceOption.Nutrition:
                    stubLR.Intents.Add(new IntentRecommendation("Nutri.Query", 1));
                    await NutriQueryIntent(context, stubLR);
                    break;

                case ServiceOption.Recommendation:
                    stubLR.Intents.Add(new IntentRecommendation("Diet.Recommend", 1));
                    await DietRecommendIntent(context, stubLR);
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
                await context.PostAsync($"{KBNotFound}");
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

            IList<string> foods = GetEntities("Food.Name", result);
            IList<string> unknownFoods = new List<string>();
            string reply = "";

            /*/TEST
            IList<FoodData2> test = await GetFoodDataWithQty(result);
            foreach(var pp in test)
                reply += $"{pp.ToString()}//";
            await context.PostAsync(reply);
            reply = "";
            //TEST*/

            // if the trigger is not from Again intent, update the last intent
            if (!MatchIntent(result, new string[] { "Again", "None" }))
            {
                ResetFlags();
                lastIntent = "Calories.Query";
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
                        reply += $"**{results[i].RowKey}** has **{results[i].Calories} kcal** of calories{((i != results.Count - 1) ? "," : "")}\n";
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
                    for (int i = 0; i < unknownFoods.Count; i++)
                        reply += $"**{unknownFoods[i]}**" +
                            $"{((i != (unknownFoods.Count - 1)) ? (unknownFoods.Count > 1) ? (i != (unknownFoods.Count - 2)) ? ", " : " and " : "" : ".")}";
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
                            reply += $"**{RecentFoods[i].RowKey}** has **{RecentFoods[i].Calories} kcal** of calories" +
                                $"{((i != RecentFoods.Count - 1) ? "," : "")}\n";
                        }

                        IntentFin = true;

                    } else // handing there is no food being quried beforehand
                    {
                        IntentFin = false;
                        // asking for food if this is first time user query does not contain foods
                        AskedForFood = true;
                        reply += "Alright, feed us some foods then.";
                    }
                }
                else if ((Nutris.Count > 1 || !Nutris.Contains("calories")) && !CalledCaloriesOptions)
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
                lastIntent = "Nutri.Query";
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
                            Dictionary<string, double> nutriValues = results[i].GetNutriValues();
                            reply += $"**{results[i].RowKey}** contains\n";
                            for (int j = 0; j < nutris.Count; j++)
                            {
                                reply += $"**{nutriValues[nutris[j]]} " +
                                    $"{(nutris[j].Equals("calories") ? "kCal" : "grams")}** of **{nutris[j]}**{((j != nutris.Count - 1) ? "," : "")}\n";
                            }
                            reply += ".\n";
                        } else
                        {
                            if (foods.Count == results.Count)
                                unknownFoods.Add(foods[i]);
                        }
                    }
                    if (reply.Length > 2)
                        reply = reply.Substring(0, reply.Length - 2);

                // else if user is prompted for nutrition
                } else if (AskedForNutri)
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
                                Dictionary<string, double> nutriValues = results[i].GetNutriValues();
                                reply += $"**{results[i].RowKey}** contains\n";
                                for (int j = 0; j < nutris.Count; j++)
                                {
                                    reply += $"**{nutriValues[nutris[j]]} " +
                                        $"{(nutris[j].Equals("calories") ? "kCal" : "grams")}** of **{nutris[j]}**{((j != nutris.Count - 1) ? "," : "")}\n";
                                }
                                reply += ".\n";
                            }
                            else
                            {
                                if (CachedFood.Count == results.Count)
                                    unknownFoods.Add(CachedFood[i]);
                            }
                        }
                        if (reply.Length > 2)
                            reply = reply.Substring(0, reply.Length - 2);

                    } else
                    {
                        reply += "Seems like we didn't spot any valid nutrition. Mind trying again?";
                        IntentFin = false;
                    }
                
                // else if user is prompted for foods
                } else if (AskedForFood2)
                {
                    if(foods.Count > 0)
                    {
                        AskedForFood2 = false;
                        results = await FoodInfoQuery(foods);
                        AddFoods(results);

                        for (int i = 0; i < results.Count; i++)
                        {
                            if (results[i] != null)
                            {
                                Dictionary<string, double> nutriValues = results[i].GetNutriValues();
                                reply += $"**{results[i].RowKey}** contain\n";
                                for (int j = 0; j < CachedNutri.Count; j++)
                                {
                                    reply += $"**{nutriValues[CachedNutri[j]]} " +
                                        $"{(CachedNutri[j].Equals("calories") ? "kCal" : "gram")}** of **{CachedNutri[j]}**{((j != CachedNutri.Count - 1) ? "," : "")}\n";
                                }
                                reply += ".\n";
                            }
                            else
                            {
                                if (foods.Count == results.Count)
                                    unknownFoods.Add(foods[i]);
                            }
                        }
                        if (reply.Length > 2)
                            reply = reply.Substring(0, reply.Length - 2);

                    } else
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
                            Dictionary<string, double> nutriValues = RecentFoods[i].GetNutriValues();
                            reply += $"**{RecentFoods[i].RowKey}** contain\n";
                            for (int j = 0; j < nutris.Count; j++)
                            {
                                reply += $"**{nutriValues[nutris[j]]} " +
                                    $"{(nutris[j].Equals("calories") ? "kCal" : "grams")}** of **{nutris[j]}**{((j != nutris.Count - 1) ? "," : "")}\n";
                            }
                            reply += ".\n";
                        }
                        reply = reply.Substring(0, reply.Length - 2);
                        IntentFin = true;
                        await context.PostAsync(reply);
                        context.Wait(MessageReceived);
                        return;

                    } else
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

                    for (int i = 0; i < CachedNutri.Count; i++)
                        reply += $"**{CachedNutri[i]}**" +
                            $"{((i != (CachedNutri.Count - 1)) ? (CachedNutri.Count > 1) ? (i != (CachedNutri.Count - 2)) ? ", " : " and " : "" : "")}";

                    reply += " of which food you want to find out?";
                    AskedForFood2 = true;

                }
                // handling displaying all nutritions info
                else if (foods.Count > 0 && nutris.Count == 0)
                {
                    // remebering foods entered
                    CachedFood.Clear();
                    CachedFood.AddRange(foods);

                    reply += "Displaying all nutrition info for ";
                    for (int i = 0; i < foods.Count; i++) 
                    {
                        reply += $"**{foods[i]}**{((i != (foods.Count - 1)) ? (foods.Count > 1) ? (i != (foods.Count - 2)) ? ", " : " and " : "" : "")}";
                    }
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

            if (unknownFoods.Count > 0)
            {
                reply += "\nOops, I coudn't find any info on ";
                for (int j = 0; j < unknownFoods.Count; j++)
                    reply += $"**{unknownFoods[j]}**" +
                        $"{((j != (unknownFoods.Count - 1)) ? (unknownFoods.Count > 1) ? (j != (unknownFoods.Count - 2)) ? ", " : " and " : "" : ".")}";
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
                        reply += $"**{results[i].RowKey}** contains\n{results[i].GetFullNutri()}\n";
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
                    for (int j = 0; j < unknownFoods.Count; j++)
                        reply += $"**{unknownFoods[j]}**" +
                            $"{((j != (unknownFoods.Count - 1)) ? (unknownFoods.Count > 1) ? (j != (unknownFoods.Count - 2)) ? ", " : " and " : "" : ".")}";
                }

                IntentFin = true;
                await context.PostAsync(reply);

            } else // handing prompting for only specific nutritions
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
            IList<string> foods = GetEntities("Food.Name", result);
            IList<string> group = GetEntities("User.Group", result);
            IList<string> nutris = GetEntities("Food.Nutri", result);
            IList<string> followup = GetEntities("User.FollowUp", result);

            // redirect to nutri query if detected any nutrition
            if (nutris.Count > 0)
            {
                await NutriQueryIntent(context, result);
                return;
            }

            // initialize group if user first invoke this function
            if (AgeGroupDiet.RowKey.Equals("None"))
                AgeGroupDiet = await DietInfoQuery("User.Diet", "adult");
            // else, assign user to the age group he/she specified
            else if (group.Count > 0)
                AgeGroupDiet = await DietInfoQuery("User.Diet", group[0]);

            if (!MatchIntent(result, new string[] { "Again", "None" }))
            {
                lastIntent = "Diet.Recommend";
            }

            string reply = "";

            IList<FoodData> results = new List<FoodData>();

            // if user query contains no food
            if (foods.Count == 0 && !AskedForFood3 && !ReloadedPrevFoods)
            {

                // if there is previous foods queried
                if (PrevFoods.Count > 0)
                {
                    reply += "Including all previous foods?";

                    var options = new ConfirmOption[] { ConfirmOption.Yes, ConfirmOption.No };
                    var descs = new string[] { "Yes, all of them.", "Nope, I will feed you other foods" };
                    PromptDialog.Choice<ConfirmOption>(context, ExecLoadingAllPrevFoods, options, reply, descriptions: descs);

                    return;
                }
                else
                { // else, prompt for foods
                    if (AskedForFood3)
                        reply += "Hmmm.. Seems like we didn't spot any foods. Mind trying again?";
                    else
                        reply += "Sure, show us what have you eaten lately?";
                    AskedForFood3 = true;
                    IntentFin = false;
                }

            }
            else if (foods.Count > 0 || ReloadedPrevFoods || AskedForFood3)
            {
                // if all previous foods are pre-loaded
                if (ReloadedPrevFoods)
                {
                    ReloadedPrevFoods = false;
                    results = GetRecentFoods();
                    //await context.PostAsync($"RELOADED {results.Count}");
                }
                else if (AskedForFood3 || foods.Count > 0)
                {
                    AskedForFood3 = false;
                    results = await FoodInfoQuery(foods);
                    AddFoods(results);
                }

                IntentFin = true;

                DietData UserDiet = new DietData();

                // calculate total user nutritions
                reply += "You have ate ";
                for (int i = 0; i < results.Count; i++) 
                {
                    UserDiet.AddDiet(results[i]);
                    reply += $"{results[i].RowKey}{((i != (results.Count - 1)) ? (results.Count > 1) ? (i != (results.Count - 2)) ? ", " : " and " : "" : ".")}";
                }

                // retrieve user diet and dietary plan data
                Dictionary<string, double> UserDietNutri = UserDiet.GetNutriValues();
                Dictionary<string, double> DietNutri = AgeGroupDiet.GetNutriValues();

                // define limits
                double rate = 0.4;
                double upper = 0.8;
                double lower = 0.2;

                // initialize dict to contain rate of consumption
                Dictionary<string, double> UserDietQuota = new Dictionary<string, double>();
                List<string> NutriExceed = new List<string>();
                List<string> NutriLack = new List<string>();

                var keysEnumerate = UserDietNutri.Keys.GetEnumerator();

                // determine whether each nutrition are lacking or exceeding
                while (keysEnumerate.MoveNext())
                {
                    string key = keysEnumerate.Current;
                    UserDietQuota.Add(key, UserDietNutri[key] / (DietNutri[key] * rate));
                    //reply += $"{key} = {UserDietQuota[key]}\n";
                    if (UserDietQuota[key] < lower)
                        NutriLack.Add(key);
                    else if (UserDietQuota[key] > upper)
                        NutriExceed.Add(key);
                }

                // reply about foods eaten
                await context.PostAsync(reply);
                reply = "";

                // reply about calories
                reply += $"You have taken **{UserDietNutri["calories"]} kCal** of calories in your last meal!\n";
                if (NutriExceed.Contains("calories"))
                    reply += "That's a lot! Try eating fewer in your next meal.\n";
                else if (NutriLack.Contains("calories"))
                    reply += "That's quite few.. Try eating more in your next meal.\n";

                // reply about other macronutrition
                if (NutriExceed.Count > 0)
                {
                    reply += "\nSeems like you took **excessive** amount of ";
                    for(int i = 0; i<NutriExceed.Count; i++)
                    {
                        if (!NutriExceed[i].Equals("calories"))
                        {
                            reply += $"**{NutriExceed[i]}**" +
                                $"{((i != (NutriExceed.Count - 1)) ? (NutriExceed.Count > 1) ? (i != (NutriExceed.Count - 2)) ? ", " : " and " : "" : "")}";
                        }
                    }
                    if (NutriLack.Count == 0)
                        reply += " in your last meal";
                    else
                        reply += ".";
                }
                if (NutriLack.Count > 0)
                {
                    if (NutriExceed.Count > 0 && !(NutriExceed.Count == 1 && NutriExceed.Contains("calories")))
                        reply += "\nYou are also **lacking** of ";
                    else
                        reply += "\nSeems like you are **lacking** of ";
                    for (int i = 0; i < NutriLack.Count; i++)
                    {
                        if (!NutriLack[i].Equals("calories"))
                        {
                            reply += $"**{NutriLack[i]}**" +
                                $"{((i != (NutriLack.Count - 1)) ? (NutriLack.Count > 1) ? (i != (NutriLack.Count - 2)) ? ", " : " and " : "" : "")}";
                        }
                    }
                    reply += " in your last meal.";
                }
                if (NutriLack.Count == 0 && NutriExceed.Count == 0)
                {
                    reply += "\nWow! You are doing a great job in balancing your diet.\nKeep it up in your next meal!";
                }


                await context.PostAsync(reply);

                // making reply on recommended foods
                reply = "";
                List<string> EatLess = FilterFoodType(NutriExceed, false, null);
                List<string> EatMore = FilterFoodType(NutriLack, true, EatLess);

                // print which foods to eat
                if (EatMore.Count > 0)
                {
                    reply = "In your next meal,\nyou could **consuming more** ";
                    for (int i = 0; i < EatMore.Count; i++)
                        reply += $"**{EatMore[i]}**{((i != EatMore.Count - 1) ? " and " : ".")}";
                }
                if (EatLess.Count > 0)
                {
                    if (EatMore.Count > 0)
                        reply += "\nBut try to **control** consumption on ";
                    else
                        reply += "In your next meal,\nyou should **control** consumption on ";

                    for (int i = 0; i < EatLess.Count; i++)
                        reply += $"**{EatLess[i]}**{((i != EatMore.Count - 1) ? " and " : ".")}";
                }
            }

            if (!reply.Equals(""))
                await context.PostAsync(reply);

            context.Wait(MessageReceived);
        }

        private List<string> FilterFoodType(List<string> Nutris, bool EatMore, List<string> EatLess)
        {
            var FoodTypeKey = FoodTypeNutriMap.GetEnumerator();
            int Score = 0;
            List<string> FoodName = new List<string>();

            // seeing which foods to eat
            if (Nutris.Count > 0)
            {
                List<string> curNutris;
                int curScore = 0;
                while (FoodTypeKey.MoveNext())
                {
                    curScore = 0;
                    curNutris = FoodTypeNutriMap[FoodTypeKey.Current.Key];
                    foreach (string nutri in curNutris)
                    {
                        foreach (string lack in Nutris)
                        {
                            if (nutri.Equals(lack))
                            {
                                curScore++;
                                break;
                            }
                        }
                    }
                    if (curScore >= Score)
                    {
                        if (curScore > Score)
                        {
                            Score = curScore;
                            FoodName.Clear();
                        }
                        FoodName.Add(FoodTypeKey.Current.Key);
                    }
                }
            }

            if (!EatMore)
                FoodName.Reverse();
            else
            {
                foreach (string foodname in EatLess)
                    FoodName.Remove(foodname);
            }

            if (FoodName.Count <= 2)
                return FoodName;
            else
                return FoodName.GetRange(0, 2);
        }

        private async Task ExecLoadingAllPrevFoods(IDialogContext context, IAwaitable<ConfirmOption> result)
        {
            var option = await result;

            if (option == ConfirmOption.Yes)
            {
                // load all previous foods
                List<FoodData> AllPrevFood = new List<FoodData>();
                foreach(var foodlist in PrevFoods)
                {
                    foreach(var food in foodlist)
                    {
                        if (!CheckExists(food, AllPrevFood))
                            AllPrevFood.Add(food);
                    }
                }
                PrevFoods.Clear();
                PrevFoods.Add(AllPrevFood);
                ReloadedPrevFoods = true;
                LuisResult stubLR = new LuisResult("", new List<EntityRecommendation>(), new IntentRecommendation(),
                    new List<IntentRecommendation>(), new List<CompositeEntity>());
                stubLR.Intents.Add(new IntentRecommendation("Diet.Recommend", 1));
                await DietRecommendIntent(context, stubLR);
                return;

            } else
            {
                // prompt for foods
                PrevFoods.Clear();
                await context.PostAsync("Sure, show us what have you eaten lately?");
                AskedForFood3 = true;
                IntentFin = false;
                context.Wait(MessageReceived);
            }
            
        }

        [LuisIntent("Diet.Query")]
        public async Task DietQueryIntent(IDialogContext context, LuisResult result)
        {
            //await this.ShowLuisResult(context, result);
            IList<string> foods = GetEntities("Food.Name", result);
            IList<string> nutris = GetEntities("Food.Nutri", result);
            IList<string> group = GetEntities("User.Group", result);
            IList<string> followup = GetEntities("User.FollowUp", result);

            // if this intent is invoked before or having age group query
            if (!Invoked || group.Count > 0)
            {
                // update to the age group mentioned
                if (group.Count > 0)
                    AgeGroupDiet = await DietInfoQuery("User.Diet", group[0]);
                // else, set to default age group
                else if (!Invoked)
                    AgeGroupDiet = await DietInfoQuery("User.Diet", "adult");
                Invoked = true;
            }

            if (!MatchIntent(result, new string[] { "Again", "None" }))
            {
                lastIntent = "Diet.Query";
            }

            string reply = "";
            double ratio = 0.33;
            string[] extent = { "rather low", "moderate", "moderate", "high", "rather high" };

            IList<FoodData> results;
            // if it is not a followup utterances or a followup utterance for another foods
            if ((followup.Count == 0 && foods.Count > 0) || (followup.Count > 0 && foods.Count > 0 && CachedNutri2.Count > 0))
            {
                results = await FoodInfoQuery(foods);
                AddFoods(results);
            } else // else it is a followup utterance with another nutrition
            {
                // if there is already previous food quried
                if (PrevFoods.Count > 0 && IntentFin)
                    results = GetRecentFoods();
                else
                {
                    reply += "Sure, but please make some queries first.";
                    await context.PostAsync(reply);
                    context.Wait(MessageReceived);
                    return;
                }
            }

            // managing cache for nutrition
            if (nutris.Count > 0)
            {
                CachedNutri2.Clear();
                CachedNutri2.AddRange(nutris);
            }
            else
                nutris = CachedNutri2;

            // handling normal complete utterance iwith only one food with more than one nutritions 
            // conditions:
            // 1. it is a complete full utterance
            // 2. It is a followup utterance
            // 3. It is specified with a age group and have previous food queried
            if ((foods.Count > 0 && nutris.Count > 0) || (nutris.Count > 0 && followup.Count > 0) || (PrevFoods.Count > 0 && group.Count > 0))
            {
                IList<string> unknownFoods = new List<string>();

                reply += $"For a single meal of normal **{AgeGroupDiet.RowKey}**,";
                for (int k = 0; k < results.Count; k++)
                {
                    if (results[k] != null)
                    {
                        reply += $"\n**{results[k].RowKey}** has ";
                        Dictionary<string, double> nutriValues = results[k].GetNutriValues();
                        for (int i = 0; i < nutris.Count; i++)
                        {
                            double baseline = nutriValues[nutris[i]] * ratio;
                            double currentNutri = nutriValues[nutris[i]];
                            for (double j = 0.2; j <= 1; j += 0.2)
                            {
                                if (baseline * j > currentNutri)
                                {
                                    reply += $"**{extent[(int)(j / 0.2 - 1)]}** ({currentNutri}{(nutris[i].Equals("calories") ? "kCal" : "g")})";
                                    break;
                                }
                            }
                            if (baseline < currentNutri)
                                reply += $"**{extent[4]}** ({currentNutri}{(nutris[i].Equals("calories") ? "kCal" : "g")})";
                            reply += $" **{nutris[i]}**{((i != (nutris.Count - 1)) ? (nutris.Count > 1) ? (i != (nutris.Count - 2)) ? ", " : " and " : "" : ",")}";
                        }
                        reply += ((k != (results.Count - 1)) ? (results.Count > 1) ? (k != (results.Count - 2)) ? ", " : " while " : "" : ".");
                    } else
                    {
                        if (results.Count == foods.Count)
                            unknownFoods.Add(foods[k]);
                    }
                }
                reply += ".\n";

                if (unknownFoods.Count > 0)
                {
                    reply += "Oops, I coudn't find any info on ";
                    for (int j = 0; j < unknownFoods.Count; j++)
                        reply += $"**{unknownFoods[j]}**" +
                            $"{((j != (unknownFoods.Count - 1)) ? (unknownFoods.Count > 1) ? (j != (unknownFoods.Count - 2)) ? ", " : " and " : "" : ".")}";
                }

                IntentFin = true;
            } else // pass to Nutri.Query intent if anything missing in utterances
            {
                lastIntent = "Nutri.Query";
                await NutriQueryIntent(context, result);
                return;
            }

            await context.PostAsync(reply);
            context.Wait(MessageReceived);
        }

        // terrance
        [LuisIntent("Symptoms.Food.Query")]
        public async Task SymptomsFoodQueryIntent(IDialogContext context, LuisResult result)
        {
            if (!MatchIntent(result, new string[] { "Again", "None" }))
            {
                lastIntent = result.Intents[0].Intent;
            }

            IList<string> foods = GetEntities("Food.Name", result);
            IList<string> symptoms = GetEntities("User.Symptoms", result);
            IList<String> userFollowUp = GetEntities("User.FollowUp", result);
            IList<FoodData> results;
            if (userFollowUp.Count != 0 && (foods.Count == 0 || symptoms.Count == 0) && PrevFoods.Count != 0)
            {
                results = GetRecentFoods();
            }
            else
            {
                results = await FoodInfoQuery(foods);
                AddFoods(results);

            }

            Boolean printCounter = true;

            IList<SymptomsData> symptomsResults = await SymptomsInfoQuery(symptoms);
            if (symptomsResults.Count != 0)
            {
                String printResultToUser = "";


                for (int i = 0; i < results.Count; i++)
                {
                    if (results[i] != null)
                    {
                        int foodGoodCount = 0;
                        if (string.Equals(symptoms[0], "constipation"))
                        {

                            double fibre = symptomsResults[0].Fibre;
                            double protein = symptomsResults[0].Protein;

                            if (results[i].Fibre >= fibre)
                            {
                                foodGoodCount++;
                            }
                            if (results[i].Protein >= protein)
                            {
                                foodGoodCount++;
                            }

                            if (foodGoodCount >= 2)
                                printResultToUser += results[i].RowKey + " is **highly recommended**  for constipation person \n";
                            else
                                printResultToUser += results[i].RowKey + " is **not recommended**  for constipation person \n";
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
                                printResultToUser += results[i].RowKey + " is **highly recommended** for diabetes person \n";
                            else
                                printResultToUser += results[i].RowKey + " is **not recommended** for diabetes person \n";



                        }
                        else if (string.Equals(symptoms[0], "obese"))
                        {

                            if (results[i].Calories <= 105)
                            {
                                foodGoodCount++;

                            }
                            if (foodGoodCount >= 1)
                                printResultToUser += results[i].RowKey + " is **highly recommended**  for obesity person \n";
                            else
                                printResultToUser += results[i].RowKey + " is **not recommended**  for obesity person \n";

                        }

                    }
                    else
                    {
                        printCounter = false;
                        await context.PostAsync("Sorry cant handle your request  " + "information is not found in our database");
                    }


                }
                if (printCounter)
                    await context.PostAsync(printResultToUser);

            }
            else
            {
                await context.PostAsync("Sorry, our bot cant handle your case ,because symptoms information is not found in our database, please specify only constipation,diabetes or obese");
            }



            context.Wait(MessageReceived);
        }

        [LuisIntent("Finish")]
        public async Task FinishIntent(IDialogContext context, LuisResult result)
        {
            // pass to QnA kb to look for related answer and handle help
            var qnaMakerAnswer = await domainQnAService.GetAnswer(result.Query);

            if (qnaMakerAnswer.CompareTo(KBOriginalNotFound) == 0)
            {
                await context.PostAsync($"{KBNotFound}");
            }
            else
            {
                await context.PostAsync($"{qnaMakerAnswer}");
            }

            ResetAllFlags();
            context.Wait(MessageReceived);
        }

        [LuisIntent("Cancel")]
        public async Task CancelIntent(IDialogContext context, LuisResult result)
        {
            // resetting all flags and caches
            ResetFlags();

            var options = new ConfirmOption[] { ConfirmOption.Yes, ConfirmOption.No };
            var descs = new string[] { "Yes", "Nope" };

            PromptDialog.Choice<ConfirmOption>(context, ExecCancel, options, "Alright, we heared you.\nDo you still wanna stay with us?", descriptions: descs);
            return;
            //context.Wait(MessageReceived);
            //await this.ShowLuisResult(context, result);
        }

        private async Task ExecCancel(IDialogContext context, IAwaitable<ConfirmOption> result)
        {
            var option = await result;

            if(option == ConfirmOption.Yes)
            {
                ResetAllFlags();
                await context.PostAsync("Alright, see you next time.");
            } else
            {
                await context.PostAsync("Alright, anything else to ask us?");
            }

            context.Wait(MessageReceived);
        }

        private void ResetFlags()
        {
            IntentFin = true;
            switch(lastIntent)
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

                case "Diet.Query":
                    Invoked = false;
                    CachedNutri2.Clear();
                    break;

                case "Diet.Recommned":
                    AskedForFood3 = false;
                    ReloadedPrevFoods = false;
                    break;

                default:
                    break;
            }
        }

        private void ResetAllFlags()
        {
            lastIntent = "None";
            SessionStarted = false;
            CalledCaloriesOptions = false;
            IntentFin = true;

            AskedForFood = false;

            CachedNutri.Clear();
            CachedFood.Clear();
            AskedForNutri = false;
            AskedForFood2 = false;

            PrevFoods.Clear();

            Invoked = false;
            AgeGroupDiet = new DietData();
            CachedNutri2.Clear();

            AskedForFood3 = false;
            ReloadedPrevFoods = false;
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

                case "Diet.Query":
                    await DietQueryIntent(context, result);
                    break;

                case "Diet.Recommend":
                    await DietRecommendIntent(context, result);
                    break;

                case "Symptoms.Food.Query":
                    await SymptomsFoodQueryIntent(context, result);
                    break;

                case "None":
                default:
                    await context.PostAsync($"{KBNotFound}");
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

        private IList<string> GetRawEntities(string entity, LuisResult result)
        {
            IList<string> entities = new List<string>();

            foreach (var ent in result.Entities)
            {
                if (string.Compare(entity, ent.Type) == 0)
                {
                    if (!entities.Contains(ent.Entity.ToLower()))
                        entities.Add(ent.Entity.ToLower());
                }
            }

            return entities;
        }

        private async Task<IList<FoodData2>> GetFoodDataWithQty(LuisResult result)
        {
            IList<string> foodname = GetEntities("Food.Name", result);
            IList<string> foodunit = GetEntities("Food.Unit", result);
            IList<string> rawfoodname = GetRawEntities("Food.Name", result);
            int foodcount = 0;
            int unitcount = 0;

            IList<FoodData> fooddata = await FoodInfoQuery(foodname);
            IList<FoodData2> fooddata2 = new List<FoodData2>();

            Dictionary<string, object> buffer = new Dictionary<string, object>();

            foreach(var cent in result.CompositeEntities)
            {
                int qty = 1;
                string unit = "";

                buffer.Clear();
                foreach (var ccent in cent.Children)
                    buffer.Add(ccent.Type, ccent.Value);

                var bufferCount = buffer.GetEnumerator();
                while(bufferCount.MoveNext())
                {
                    string key = bufferCount.Current.Key;
                    if (key.Equals("builtin.number"))
                        qty = (int)buffer["builtin.number"];
                    else if (key.Equals("Food.Unit"))
                    {
                        unit = foodunit[unitcount];
                        unitcount++;
                    }
                    else if (key.Equals("Food.Name"))
                    {
                        string curfoodname = (string)buffer["Food.Name"];
                        while (!curfoodname.Equals(rawfoodname[foodcount]))
                        {
                            fooddata2.Add(new FoodData2(fooddata[foodcount], 1, ""));
                            foodcount++;
                        }
                    }
                }

                fooddata2.Add(new FoodData2(fooddata[foodcount], qty, unit));
                foodcount++;
            }

            if (fooddata.Count > 0 && result.CompositeEntities.Count == 0)
            {
                foreach(var data in fooddata)
                    fooddata2.Add(new FoodData2(data, 1, ""));
            }

            return fooddata2;
        }

        private bool MatchIntent(LuisResult result, string[] intents) {
            foreach (string intent in intents) {
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

        // terrance
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

        // METHODS ON FOOD HISTORY LIST ==================================

        // check if a certain food is previosuly queried
        private bool CheckExists(FoodData f, IList<FoodData> foodlist)
        {
            foreach (var food in foodlist)
            {
                if (food.RowKey.Equals(f.RowKey))
                    return true;
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
            if(food.Count > 0)
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
        public double ServingSize { get; set; }
        public string ServingUnit { get; set; }

        public override string ToString()
        {
            return "FoodType : " + FoodType + "\nFood Name : " + this.RowKey + "\nCalories : " + this.Calories + 
                "\nFat : " + this.Fat + "\nSugar : " + this.Sugar + "\nSodium : " + this.Sodium + "\nProtein : " + 
                this.Protein + "\nCarbohydrate : " + this.Carbohydrate + "\nFibre : " + this.Fibre + 
                $"\nServingSize : {ServingSize}\nServingUnit : {ServingUnit}";
        }

        public string GetFullNutri()
        {
            return $"Calories: {Calories} kCal,\nFat: {Fat} g,\nSugar: {Sugar} " +
                $"g,\nSodium: {Sodium} g,\nProtein: {Protein} g,\nCarbohydrate: {Carbohydrate} g,\nFibre: {Fibre} g.\n";
        }

        public Dictionary<string, double> GetNutriValues()
        {
            Dictionary<string, double> buffer = new Dictionary<string, double>();
            buffer.Add("calories", Calories);
            buffer.Add("fat", Fat);
            buffer.Add("sugar", Sugar);
            buffer.Add("sodium", Sodium);
            buffer.Add("protein", Protein);
            buffer.Add("carbohydrate", Carbohydrate);
            buffer.Add("fibre", Fibre);
            return buffer;
        }
    }

    // class built ontop of FoodData
    public class FoodData2
    {
        public FoodData food { get; set; }
        public int qty { get; set; }
        public string unit { get; set; }

        public FoodData2() { }

        public FoodData2(FoodData f, int q, string u)
        {
            food = f;
            qty = q;
            unit = u;
        }

        public override string ToString()
        {
            return $"FODD : ${food.ToString()} QTY : {qty} UNIT : {unit}";
        }

        public double GetSingleNutriValue(string nutri)
        {
            return 0;
        }

        public Dictionary<string, double> GetNutriValues()
        {
            Dictionary<string, double> buffer = new Dictionary<string, double>();
            buffer.Add("calories", food.Calories);
            buffer.Add("fat", food.Fat);
            buffer.Add("sugar", food.Sugar);
            buffer.Add("sodium", food.Sodium);
            buffer.Add("protein", food.Protein);
            buffer.Add("carbohydrate", food.Carbohydrate);
            buffer.Add("fibre", food.Fibre);
            return buffer;
        }
    }

    // terrance
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

    // diet table entity class
    public class DietData : TableEntity
    {
        
        public DietData(string domain, string id)
        {
            this.PartitionKey = domain; //ENTITY NAME
            this.RowKey = id; //FOOD NAME, USER GROUP, SYMPTOM NAME
        }

        public DietData() {
            RowKey = "None";
            Calories = 0;
            Fat = 0;
            Sugar = 0;
            Sodium = 0;
            Protein = 0;
            Carbohydrate = 0;
            Fibre = 0;
        }

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

        public void AddDiet(FoodData food)
        {
            Calories += food.Calories;
            Fat += food.Fat;
            Sugar += food.Sugar;
            Sodium += food.Sodium;
            Protein += food.Protein;
            Carbohydrate += food.Carbohydrate;
            Fibre += food.Fibre;
        }

        public Dictionary<string, double> GetNutriValues()
        {
            Dictionary<string, double> buffer = new Dictionary<string, double>();
            buffer.Add("calories", Calories);
            buffer.Add("fat", Fat);
            buffer.Add("sugar", Sugar);
            buffer.Add("sodium", Sodium);
            buffer.Add("protein", Protein);
            buffer.Add("carbohydrate", Carbohydrate);
            buffer.Add("fibre", Fibre);
            return buffer;
        }
    }
}