using System;
using Telegram.Bot;
using Telegram.Bot.Args;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using System.Text.RegularExpressions;

namespace TelegramDay2
{
	class Program
	{
		private static TelegramBotClient Bot;
		private static Quiz Quizobj;
		private static Dictionary<long, QuesetionState> QuesetionStates;
		//Изменил способ хранения, теперь два ключа Id чата и Id пользователя, чтобы хранить викторины разных чатов раздельно
		private static Dictionary<Tuple<long, long>, int> UserScores;
		//Словарь с username'ами пользователей
		private static Dictionary<long, string> names;
		private static string scoresFileName = "scores.json",
								statesFileName = "states.json",
								namesFileName = "names.json";


		static void Main(string[] args)
		{
			//Загрузки файлов
			//файл состояний создан чтобы сохранить состояние паузы
			//В случае аварийного завершения программы
			UserScores = new Dictionary<Tuple<long, long>, int>();
			QuesetionStates = new Dictionary<long, QuesetionState>();
			names = new Dictionary<long, string>();
			if (File.Exists(scoresFileName))
			{
				var scoresjson = File.ReadAllText(scoresFileName);
				//пришлось украсть и дописать кастомный десериалайзер, стандартный не справлялся с туплами в словаре
				UserScores = JsonConvert.DeserializeObject<Dictionary<Tuple<long, long>, int>>(scoresjson, new TupleKeyConverter());
			}
			if (File.Exists(statesFileName))
			{
				var statesjson = File.ReadAllText(statesFileName);
				QuesetionStates = JsonConvert.DeserializeObject<Dictionary<long, QuesetionState>>(statesjson);
			}
			if (File.Exists(namesFileName))
			{
				var namesjson = File.ReadAllText(namesFileName);
				names = JsonConvert.DeserializeObject<Dictionary<long, string>>(namesjson);
			}

			Quizobj = new Quiz();
			string token = "1709775323:AAHW1MwNGMjK5Jl7NrSv2SgE1XOQ8QIMT44";
			Bot = new TelegramBotClient(token);
			Bot.OnMessage += BotOnMessage;
			Bot.StartReceiving();
			Console.ReadLine();

			//Сохранение файлов при выходе
			saveScores();
			saveStates();
			saveNames();
		}
		private static async void BotOnMessage(object sender, MessageEventArgs e)
		{
			var chatId = e.Message.Chat.Id;
			long userId = e.Message.From.Id;
			//Тупл в явном виде, чтобы избежать конфликтов и путаницы
			var Ids = new Tuple<long, long>(chatId, userId);
			var msg = e.Message.Text;

			//Добавление новых имён в словарь
			if (!names.ContainsKey(userId))
			{
				names[userId] = $"{ e.Message.From.Username} {e.Message.From.FirstName} {e.Message.From.LastName}";
				saveNames();
			}

			//Обработчик сообщений
			switch (msg)
			{
				case "/start":
					if (!QuesetionStates.ContainsKey(chatId) || !QuesetionStates[chatId].gameIsOn)
					{
						await Bot.SendTextMessageAsync(chatId,
						"Чат-бот Викторина, отгадывайте слова - получайте очки. \n" +
						"За каждую неправильную попытку вы теряете одно очко, отгадывая слова вы получаете кол-во очков, раное количеству неизвесных букв.\n" +
						"Так что неправильные попытки не только отнимаю у вас очки, но и уменьшают количество очков получаемое тем, кто отгадает слово!\n" +
						"Удачи!");
						NewRound(chatId);
					}
					else
						await Bot.SendTextMessageAsync(chatId, "Игра уже начата, остановите игру чтобы начать новый раунд");
					break;

				case "/help":
					await Bot.SendTextMessageAsync(chatId, "/start - начинает новую игру" + "\n" +
															"/stop - останавливает игру" + "\n" +
															"/help - высвечивает это окно" + "\n" +
															"/stats - высвечивает топ10 игроков" + "\n" +
															"/pause - ставит текущий раунд на паузу" + "\n" +
															"/unpause - снимает раунд с паузы");
					break;

				case "/stats":
					string answrMsg = "Топ 10 игроков \n";
					//массив топ 10 игроков этого чата, плюс сортировка
					var ansarr = (from s in UserScores
								  where s.Key.Item1 == chatId
								  orderby s.Value descending
								  select s).Take(10)
									.ToArray();

					for (int i = 0; i < ansarr.Length & i < 10; i++)
					{
						answrMsg += $@"{ i + 1}. ";
						answrMsg += $@"<a href=""tg://user?id={ansarr[i].Key.Item2}""> {names[ansarr[i].Key.Item2].ToString().Trim()} </a >";
						answrMsg += $@":			 {ansarr[i].Value} {getWord(ansarr[i].Value)}" + "\n";
					}
					saveScores();
					await Bot.SendTextMessageAsync(chatId, answrMsg, Telegram.Bot.Types.Enums.ParseMode.Html);
					break;

				case "/stop":
					if (QuesetionStates.ContainsKey(chatId) && QuesetionStates[chatId].gameIsOn)
					{
						QuesetionStates.Remove(chatId);
						saveScores();
						await Bot.SendTextMessageAsync(chatId, "Игра остановлена");
					}
					break;

				case "/pause":
					if (QuesetionStates.ContainsKey(chatId) && QuesetionStates[chatId].gameIsOn)
					{
						QuesetionStates[chatId].gameIsOn = false;
						saveScores();
						saveStates();
						await Bot.SendTextMessageAsync(chatId, "Игра приостановлена, для подолжение напишите /unpause");
					}
					break;

				default:
					if (msg != null)
						if (QuesetionStates.TryGetValue(chatId, out var questionstate) && (QuesetionStates[chatId].gameIsOn || msg == "/unpause"))
						{
							if (!questionstate.gameIsOn)
								await Bot.SendTextMessageAsync(chatId, "Игра продолжается");
							//Прoверка на существование очков
							if (!UserScores.ContainsKey(Ids))
							{
								UserScores[Ids] = 0;
							}
							//замена ё на е во входящем сообщении
							Regex rx = new Regex("ё");
							var checkMsg = rx.Replace(msg, "е");
							//Проверка на истинность ответа
							if (checkMsg.Trim().ToLowerInvariant() == questionstate.QA.Answer.Trim().ToLowerInvariant())
							{
								UserScores[Ids] += questionstate.QA.Answer.Length - questionstate.Opened;
								saveScores();
								var winMsg = $@"Правильно! Это - { questionstate.QA.Answer} У вас: {UserScores[Ids]} {getWord(UserScores[Ids])}";
								await Bot.SendTextMessageAsync(chatId, winMsg);
								NewRound(chatId);
							}

							//Действия при неверном ответе
							else
							{
								if (questionstate.gameIsOn)
								{
									//очки отнимаются, а подсказка увеличивается, только если игра идёт
									//сделано, чтобы избежать неправильного поведения при снятии с паузы
									UserScores[Ids]--;
									questionstate.Opened++;
								}
								else
									questionstate.gameIsOn = true;
								await Bot.SendTextMessageAsync(chatId, questionstate.DisplayQuestion);
								//Проверка конца раунда
								if (questionstate.IsEnd)
								{
									var looseMsg = $"Никто не отгадал! Это было - {questionstate.QA.Answer}" + "\n";
									await Bot.SendTextMessageAsync(chatId, looseMsg);
									saveScores();
									NewRound(chatId);
								}
							}
						}
					break;
			}

		}
		//сохранение очков
		public static void saveScores()
		{
			var Json = JsonConvert.SerializeObject(UserScores);
			File.WriteAllText(scoresFileName, Json);

		}

		//сохранение состояний
		public static void saveStates()
		{
			var Json = JsonConvert.SerializeObject(QuesetionStates);
			File.WriteAllText(statesFileName, Json);
		}
		public static void saveNames()
		{
			var Json = JsonConvert.SerializeObject(names);
			File.WriteAllText(namesFileName, Json);
		}

		//Склонение слов
		public static string getWord(int input)
		{
			input = Math.Abs(input);

			//Проверка на деление, при возрастании числа идёт прогрессия
			// 1 очко, 2-4 очка 0, 5-19, очков
			// Далее паттерн 0 - 9 повторяется каждые 10 очков до 100
			// Дальнейшее нас не интересует в данном конексте
			if (input > 20)
				input %= 10;
			switch (input)
			{
				case 1:
					return "очко";
				case 2:
				case 3:
				case 4:
					return "очка";
				default:
					return "очков";
			}
		}
		public static void NewRound(long Id)
		{

			QuestionItem quest = Quizobj.GetQuestionItem();
			QuesetionStates[Id] = new QuesetionState()
			{
				QA = quest,
				gameIsOn = true
			};
			Bot.SendTextMessageAsync(Id, QuesetionStates[Id].DisplayQuestion);
		}
	}

	//кастомный десериалайзер со stackoverflow, частично дописанный
	public class TupleKeyConverter : JsonConverter
	{
		public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
		{
			Tuple<long, long> _tuple = null;
			var _dict = new Dictionary<Tuple<long, long>, int>();

			//loop through the JSON string reader
			while (reader.Read())
			{
				// check whether it is a property
				if (reader.TokenType == JsonToken.PropertyName)
				{
					string readerValue = reader.Value.ToString();
					if (reader.Read())
					{
						// check if the property is tuple (Dictionary key)
						if (readerValue.Contains('(') && readerValue.Contains(')'))
						{
							string[] result = ConvertTuple(readerValue);

							if (result == null)
								continue;

							// Custom Deserialize the Dictionary key (Tuple)
							_tuple = Tuple.Create<long, long>(long.Parse(result[0].Trim()), long.Parse(result[1].Trim()));

							// Custom Deserialize the Dictionary value
							var _value = int.Parse(serializer.Deserialize(reader).ToString());

							_dict.Add(_tuple, _value);
						}
						else
						{
							// Deserialize the remaining data from the reader
							serializer.Deserialize(reader);
							break;
						}
					}
				}
			}
			return _dict;
		}

		public string[] ConvertTuple(string _string)
		{
			string tempStr = null;

			// remove the first character which is a brace '('
			if (_string.Contains('('))
				tempStr = _string.Remove(0, 1);

			// remove the last character which is a brace ')'
			if (_string.Contains(')'))
				tempStr = tempStr.Remove(tempStr.Length - 1, 1);

			// seperate the Item1 and Item2
			if (_string.Contains(','))
				return tempStr.Split(',');

			return null;
		}
		public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
		{
			serializer.Serialize(writer, value);
		}
		public override bool CanConvert(Type objectType)
		{
			return true;
		}
	}
	public class Quiz
	{
		private Random random;
		private int count;
		public List<QuestionItem> Quesions;
		public Quiz(string path = "data.txt")
		{
			random = new Random();
			var lines = File.ReadAllLines(path);
			Quesions = lines
			   .Select(s => s.Split('|'))
			   .Select(s => (new QuestionItem
			   {
				   Question = s[0],
				   Answer = s[1]
			   }))
			   .ToList();
		}
		public QuestionItem GetQuestionItem()
		{
			if (count < 1)
			{
				count = Quesions.Count();
			}
			var index = random.Next(count - 1);
			var question = Quesions[index];

			Quesions.RemoveAt(index);
			Quesions.Add(question);
			count--;
			return question;
		}
	}
	public class QuestionItem
	{
		public string Question { get; set; }
		public string Answer { get; set; }
	}

	public class QuesetionState
	{

		public bool gameIsOn { get; set; }
		public QuestionItem QA { get; set; }
		public int Opened { get; set; }
		public bool IsEnd => !(Opened < QA.Answer.Length);
		public bool win { get; set; }
		public string hint => QA.Answer
					.Substring(0, Opened)
					.PadRight(QA.Answer.Length, '_');
		public string DisplayQuestion => $"{QA.Question} {QA.Answer.Length} {getWord()} \n{this.hint}";

		public string getWord()
		{
			var word = QA.Answer.Length;
			//Проверка на деление, при возрастании числа идёт прогрессия
			// 1 буква; 2-4 буквы; 0, 5-19 букв
			// Далее паттерн 0 - 9 повторяется каждые 10 очков до 100
			// Дальнейшее нас не интересует в данном конексте
			if (word > 20)
				word %= 10;
			switch (word)
			{
				case 1:
					return "буква";
				case 2:
				case 3:
				case 4:
					return "буквы";
				default:
					return "букв";
			}
		}
	}
}
