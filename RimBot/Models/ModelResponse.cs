namespace RimBot.Models
{
    public class ModelResponse
    {
        public bool Success { get; set; }
        public string Content { get; set; }
        public string ErrorMessage { get; set; }
        public string RawJson { get; set; }
        public int TokensUsed { get; set; }

        public static ModelResponse FromError(string errorMessage)
        {
            return new ModelResponse
            {
                Success = false,
                Content = null,
                ErrorMessage = errorMessage,
                RawJson = null,
                TokensUsed = 0
            };
        }
    }
}
