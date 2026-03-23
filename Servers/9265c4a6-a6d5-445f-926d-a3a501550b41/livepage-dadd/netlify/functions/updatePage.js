import { createClient } from "@supabase/supabase-js";

const supabase = createClient(
  process.env.SUPABASE_URL,
  process.env.SUPABASE_KEY
);

export async function handler(event) {
  try {
    const { slug, title, content } = JSON.parse(event.body);

    if (!slug || !title || !content) {
      return {
        statusCode: 400,
        body: JSON.stringify({
          error: "Missing required fields: slug, title, content"
        })
      };
    }

    // Update the page in Supabase
    const { data, error } = await supabase
      .from("pages")
      .update({
        title,
        content,
        updated_at: new Date().toISOString()
      })
      .eq("slug", slug)
      .select();

    if (error) {
      console.error("Supabase update error:", error);
      return {
        statusCode: 500,
        body: JSON.stringify({
          error: "Supabase update failed",
          message: error.message,
          details: error.details || null,
          hint: error.hint || null,
          code: error.code || null
        })
      };
    }

    if (!data || data.length === 0) {
      return {
        statusCode: 404,
        body: JSON.stringify({
          error: "Page not found"
        })
      };
    }

    return {
      statusCode: 200,
      body: JSON.stringify({
        message: "Page updated successfully!",
        page: data[0]
      })
    };

  } catch (err) {
    console.error("Function execution error:", err);
    return {
      statusCode: 500,
      body: JSON.stringify({
        error: "Function failed",
        message: err.message,
        stack: err.stack
      })
    };
  }
}
