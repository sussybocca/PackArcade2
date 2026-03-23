from flask import Flask, jsonify
from flask_cors import CORS

app = Flask(__name__)
CORS(app)

@app.route('/api/hello')
def hello():
    return jsonify({'message': 'Hello from Python backend!'})

@app.route('/api/data')
def data():
    return jsonify({'items': ['one', 'two', 'three']})

if __name__ == '__main__':
    app.run(host='0.0.0.0', port=5000, debug=True)