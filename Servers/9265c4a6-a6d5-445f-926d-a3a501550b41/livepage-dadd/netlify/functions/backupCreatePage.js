import { createClient } from "@supabase/supabase-js";
import crypto from "crypto";
import fetch from "node-fetch"; // needed for serverless Netlify functions

const supabase = createClient(
  process.env.SUPABASE_URL,
  process.env.SUPABASE_KEY
);

// Generate random secure page name
function generateRandomSlug() {
  return "l_" + crypto.randomBytes(4).toString("hex");
}

// Text moderation using your rules
async function moderateText(content, category) {
  const lowered = content.toLowerCase();

  if (category === "family") {
    const forbiddenFamily = ["nudity", "sex act"];
    for (const word of forbiddenFamily) {
      if (lowered.includes(word)) {
        throw new Error("Family-friendly content only.");
      }
    }
  } else if (category === "18+") {
    const forbidden18Plus = ["illegal", "child", "nudity"];
    for (const word of forbidden18Plus) {
      if (lowered.includes(word)) {
        throw new Error("This content is not allowed and has been flagged.");
      }
    }
  }

  return true;
}

// Image moderation rules
async function moderateImage(imageUrl, category) {
  if (!imageUrl) return true;

  // Reject nudity
  const nudityWords = ["nudity", "naked"];
  const lowered = imageUrl.toLowerCase();
  for (const word of nudityWords) {
    if (lowered.includes(word)) {
      throw new Error(
        "This image has bad content and has been flagged. Repeated violations will block posting."
      );
    }
  }

  // Only allow bikinis for 18+
  if (category === "family") {
    throw new Error("Images are not allowed in family-friendly pages.");
  }

  return true;
}

// Google Age Verification via OAuth code
async function verifyGoogleAge(oauthCode, redirectUri) {
  if (!oauthCode) return false;

  // Exchange code for access token using Netlify secrets
  const tokenResp = await fetch("https://oauth2.googleapis.com/token", {
    method: "POST",
    headers: { "Content-Type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams({
      code: oauthCode,
      client_id: process.env.GOOGLE_CLIENT_ID,
      client_secret: process.env.GOOGLE_CLIENT_SECRET,
      redirect_uri: redirectUri,
      grant_type: "authorization_code"
    })
  });

  if (!tokenResp.ok) return false;

  const tokenData = await tokenResp.json();
  const accessToken = tokenData.access_token;
  if (!accessToken) return false;

  // Call People API to get birthday
  const resp = await fetch(
    "https://people.googleapis.com/v1/people/me?personFields=birthdays",
    { headers: { Authorization: `Bearer ${accessToken}` } }
  );

  if (!resp.ok) return false;
  const data = await resp.json();
  if (!data.birthdays || !data.birthdays[0].date) return false;

  const birth = data.birthdays[0].date;
  const birthDate = new Date(birth.year, birth.month - 1, birth.day);
  const age = Math.floor((Date.now() - birthDate.getTime()) / (1000 * 60 * 60 * 24 * 365.25));

  return age >= 18;
}

export async function handler(event) {
  try {
    const { contentType, usageType, content, category, googleCode, redirectUri, imageUrl } = JSON.parse(event.body);

    if (!contentType || !usageType || !content || !category) {
      return {
        statusCode: 400,
        body: JSON.stringify({ error: "Missing required fields" })
      };
    }

    // Verify age if 18+
    let ageVerified = false;
    if (category === "18+") {
      ageVerified = await verifyGoogleAge(googleCode, redirectUri);
      if (!ageVerified) {
        return {
          statusCode: 403,
          body: JSON.stringify({ error: "You must verify your age to post 18+ content" })
        };
      }
    }

    // Moderate text and image content
    await moderateText(content, category);
    await moderateImage(imageUrl, category);

    const slug = generateRandomSlug();

    const { data, error } = await supabase
      .from("pages")
      .insert({
        title: "Untitled Page",
        slug,
        content_type: contentType,
        usage_type: usageType,
        content,
        category,
        age_verified: ageVerified,
        image_url: imageUrl || null
      })
      .select();

    if (error) {
      return {
        statusCode: 500,
        body: JSON.stringify({ error: "Supabase insert failed", message: error.message })
      };
    }

    return {
      statusCode: 200,
      body: JSON.stringify({
        message: "Page created successfully",
        url: `/l/${slug}`,
        page: data[0]
      })
    };
  } catch (err) {
    return {
      statusCode: 500,
      body: JSON.stringify({ error: "Function failed", message: err.message })
    };
  }
}
