namespace RimBot.Models
{
    public class ContentPart
    {
        public string Type { get; set; }
        public string Text { get; set; }
        public string MediaType { get; set; }
        public string Base64Data { get; set; }

        public static ContentPart FromText(string text)
        {
            return new ContentPart { Type = "text", Text = text };
        }

        public static ContentPart FromImage(string base64Data, string mediaType)
        {
            return new ContentPart { Type = "image_url", MediaType = mediaType, Base64Data = base64Data };
        }
    }
}
