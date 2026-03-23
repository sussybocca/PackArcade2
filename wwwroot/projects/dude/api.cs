using Microsoft.AspNetCore.Mvc;

[Route("api/[controller]")]
[ApiController]
public class HelloController : ControllerBase
{
    [HttpGet]
    public IActionResult Get()
    {
        return Ok(new { message = "Hello from C# backend!" });
    }
    
    [HttpGet("data")]
    public IActionResult GetData()
    {
        return Ok(new { items = new[] { "one", "two", "three" } });
    }
}