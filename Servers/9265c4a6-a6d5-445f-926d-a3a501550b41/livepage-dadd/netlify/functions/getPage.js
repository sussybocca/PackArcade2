import { createClient } from "@supabase/supabase-js";

const supabase = createClient(
  process.env.SUPABASE_URL,
  process.env.SUPABASE_KEY
);

// Helper to escape HTML (prevent XSS)
function escapeHtml(text = "") {
  return text
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;");
}

// Normalize slug from URL to match createPage format
function normalizeSlug(slug) {
  return slug.toLowerCase().trim().replace(/\s+/g, "-");
}

export async function handler(event) {
  // Extract slug from query param OR from the path (Netlify :splat)
  const rawSlug =
    event.queryStringParameters?.slug ||
    event.path?.split("/l/")[1]; // grabs everything after /l/

  if (!rawSlug) {
    return {
      statusCode: 400,
      headers: { "Content-Type": "text/html" },
      body: "<h1>Missing slug</h1>"
    };
  }

  const slug = normalizeSlug(rawSlug);

  // Fetch page from Supabase
  const { data: page, error: pageError } = await supabase
    .from("pages")
    .select("*")
    .eq("slug", slug)
    .single();

  if (pageError || !page) {
    return {
      statusCode: 404,
      headers: { "Content-Type": "text/html" },
      body: `<h1>Page not found</h1>`
    };
  }

  // Fetch posts related to this page
  const { data: posts } = await supabase
    .from("posts")
    .select("*")
    .eq("page_slug", slug)
    .order("created_at", { ascending: false });

  const postsHtml = (posts || [])
    .map(post => `
      <div class="post">
        ${post.content ? `<p>${escapeHtml(post.content)}</p>` : ""}
        ${post.image_url ? `<img src="${post.image_url}" />` : ""}
        <small>${new Date(post.created_at).toLocaleString()}</small>
      </div>
    `)
    .join("");

  const html = `
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8" />
  <title>${escapeHtml(page.title)} — LivePage</title>
  <meta name="viewport" content="width=device-width, initial-scale=1.0" />
  <meta name="description" content="${escapeHtml(page.title)} on LivePage" />
  <style>
    body { font-family: system-ui, sans-serif; background: #0f1115; color: white; margin: 0; padding: 24px; }
    .container { max-width: 720px; margin: auto; }
    h1 { margin-bottom: 4px; }
    .meta { font-size: 0.85rem; opacity: 0.7; margin-bottom: 24px; }
    .post { background: #161a22; padding: 16px; border-radius: 12px; margin-bottom: 16px; }
    .post img { max-width: 100%; border-radius: 8px; margin-top: 10px; }
    small { display: block; margin-top: 10px; opacity: 0.6; }
  </style>
</head>
<body>
  <div class="container">
    <h1>${escapeHtml(page.title)}</h1>
    <div class="meta">
      ${page.content_type} · ${page.usage_type}
    </div>
    ${postsHtml || "<p>No posts yet.</p>"}
  </div>
</body>
</html>
`;

  return {
    statusCode: 200,
    headers: { "Content-Type": "text/html" },
    body: html
  };
}
