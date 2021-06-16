using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ParsnipMediaProcessor
{
    public static class ConsoleEx
    {
        public static bool Decision(string question)
        {
            Console.WriteLine($"{question} (y/n)");
            var response = Console.ReadLine();
            switch (response.ToLower())
            {
                case "y":
                    return true;
                case "n":
                    return false;
                default:
                    Error("Input {response} not recognised. Input must be either \"y\" or \"n\"");
                    return Decision(question);
            }
        }

        public static void Success(string text)
        {
            WriteLine(text, ConsoleColor.Green);
        }

        public static void Error(string text)
        {
            WriteLine(text, ConsoleColor.Red);
        }

        public static void WriteLine(string text, ConsoleColor color)
        {
            Console.ForegroundColor = color;
            Console.WriteLine(text);
            Console.ResetColor();
        }

        public static void Write(string text, ConsoleColor color)
        {
            Console.ForegroundColor = color;
            Console.Write(text);
            Console.ResetColor();
        }

    }
}
