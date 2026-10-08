namespace ConfidentialDataMasker.Models;

public sealed class RedactionRect
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }

    public RedactionRect() { }

    public RedactionRect(double x, double y, double width, double height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public bool Intersects(SuggestedRect other) =>
        X < other.X + other.Width && other.X < X + Width &&
        Y < other.Y + other.Height && other.Y < Y + Height;
}
